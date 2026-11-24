using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Text;

namespace Zelvo.Channels;

/// <summary>A file that came with an email.</summary>
/// <param name="FileName">The name the sender gave it.</param>
/// <param name="Content">Its bytes.</param>
public sealed record EmailFile(string FileName, byte[] Content);

/// <summary>What Zelvo reads from an email.</summary>
/// <param name="FromAddress">The sender's address (From), lower case; null when there is none or several.</param>
/// <param name="FromName">The sender's display name.</param>
/// <param name="Subject">Subject, one line.</param>
/// <param name="MessageId">Its <c>Message-ID</c>, without angle brackets.</param>
/// <param name="ThreadIds">Message-IDs it answers: <c>In-Reply-To</c> first, then <c>References</c> (newest first).</param>
/// <param name="Text">The new text, with quoted earlier emails and signatures' quote blocks removed.</param>
/// <param name="FullText">The whole text part (for an email without new text above the quote).</param>
/// <param name="Files">Attachments (inline images left out).</param>
/// <param name="AutoReason">Why Zelvo leaves it alone (<c>auto-reply</c>, <c>bulk</c>), or null.</param>
/// <param name="DmarcFailed">The receiving mail server recorded <c>dmarc=fail</c> for it.</param>
/// <param name="Language">The language the text looks written in (<c>ar</c> when mostly Arabic letters, else <c>en</c>).</param>
public sealed record ParsedEmail(
    string? FromAddress,
    string? FromName,
    string Subject,
    string? MessageId,
    IReadOnlyList<string> ThreadIds,
    string Text,
    string FullText,
    IReadOnlyList<EmailFile> Files,
    string? AutoReason,
    bool DmarcFailed,
    string Language);

/// <summary>
/// Reads incoming emails (task E2): sender, threading headers, the new text without the quoted
/// thread, attachments, and the signs of automatic mail that must never become tickets
/// (RFC 3834 <c>Auto-Submitted</c>, <c>Precedence</c>, Microsoft's <c>X-Auto-Response-Suppress</c>,
/// mailing lists' <c>List-Id</c> with <c>Precedence: list</c>, delivery reports).
/// </summary>
public static partial class EmailParsing
{
    /// <summary>Most attachments taken from one email.</summary>
    public const int MaxFiles = 20;

    /// <summary>Reads <paramref name="message"/>.</summary>
    public static ParsedEmail Parse(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var senders = message.From.Mailboxes.ToList();
        var from = senders.Count == 1 ? senders[0] : null;
        var address = from?.Address is { Length: > 0 and <= 254 } a && a.Contains('@', StringComparison.Ordinal) ? a.Trim().ToLowerInvariant() : null;
        var subject = OneLine(message.Subject ?? string.Empty);

        var threadIds = new List<string>();
        if (Clean(message.InReplyTo) is { } inReplyTo)
        {
            threadIds.Add(inReplyTo);
        }

        foreach (var reference in message.References.Reverse())
        {
            if (Clean(reference) is { } id && !threadIds.Contains(id, StringComparer.Ordinal))
            {
                threadIds.Add(id);
            }
        }

        var full = BodyText(message);
        var text = StripQuoted(full);
        return new ParsedEmail(
            address,
            from?.Name is { Length: > 0 } name ? OneLine(name) : null,
            subject,
            Clean(message.MessageId),
            [.. threadIds.Take(50)],
            text,
            full,
            Files(message),
            AutoReason(message),
            DmarcFailed(message),
            LooksArabic(subject + " " + text) ? "ar" : "en");
    }

    /// <summary>Why <paramref name="message"/> is automatic mail Zelvo leaves alone, or null.</summary>
    public static string? AutoReason(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var headers = message.Headers;
        if (headers["Auto-Submitted"] is { } auto && !auto.Trim().StartsWith("no", StringComparison.OrdinalIgnoreCase))
        {
            return "auto-reply";
        }

        if (headers["X-Autoreply"] is not null || headers["X-Autorespond"] is not null || headers["X-Auto-Response-Suppress"] is { } suppress
            && (suppress.Contains("All", StringComparison.OrdinalIgnoreCase) || suppress.Contains("AutoReply", StringComparison.OrdinalIgnoreCase)))
        {
            return "auto-reply";
        }

        if (headers["Precedence"]?.Trim().ToLowerInvariant() is "bulk" or "junk" or "list" or "auto_reply")
        {
            return "bulk";
        }

        if (headers["List-Id"] is not null || headers["List-Unsubscribe"] is not null)
        {
            return "bulk";
        }

        if (message.Body is MultipartReport || headers[HeaderId.ContentType]?.Contains("report-type=delivery-status", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "delivery-report";
        }

        return message.From.Mailboxes.FirstOrDefault()?.LocalPart.ToLowerInvariant() is "mailer-daemon" or "postmaster" ? "delivery-report" : null;
    }

    /// <summary>
    /// Whether the receiving server's <c>Authentication-Results</c> say DMARC failed. A sender can
    /// add such a header, but only to its own harm; Zelvo never treats a "pass" as proof.
    /// </summary>
    public static bool DmarcFailed(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Headers.Where(h => h.Id == HeaderId.AuthenticationResults).Any(h => DmarcFail().IsMatch(h.Value));
    }

    /// <summary>The text of the email: the plain-text part, else the HTML part turned into text.</summary>
    public static string BodyText(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = message.TextBody;
        if (string.IsNullOrWhiteSpace(text) && message.HtmlBody is { } html)
        {
            text = HtmlToText(html);
        }

        return Normalize(text ?? string.Empty);
    }

    /// <summary>
    /// Text of an HTML body: tags removed, block elements as line breaks, entities decoded; scripts
    /// and styles dropped. Nothing of the HTML is ever shown as HTML.
    /// </summary>
    public static string HtmlToText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var builder = new StringBuilder(html.Length / 2);
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader);
        var skip = 0;
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken tag:
                    if (tag.Id is HtmlTagId.Script or HtmlTagId.Style or HtmlTagId.Head || tag.Name is "title")
                    {
                        skip += tag.IsEndTag ? -1 : tag.IsEmptyElement ? 0 : 1;
                        skip = Math.Max(skip, 0);
                    }
                    else if (tag.Id is HtmlTagId.Br || (!tag.IsEndTag && tag.Id is HtmlTagId.P or HtmlTagId.Div or HtmlTagId.TR or HtmlTagId.LI or HtmlTagId.H1 or HtmlTagId.H2
                        or HtmlTagId.H3 or HtmlTagId.BlockQuote or HtmlTagId.Table) || (tag.IsEndTag && tag.Id is HtmlTagId.P or HtmlTagId.Div or HtmlTagId.Table))
                    {
                        builder.Append('\n');
                    }
                    else if (!tag.IsEndTag && tag.Id is HtmlTagId.TD or HtmlTagId.TH)
                    {
                        builder.Append(' ');
                    }

                    if (!tag.IsEndTag && tag.Id == HtmlTagId.BlockQuote)
                    {
                        builder.Append("> ");
                    }

                    break;
                case HtmlDataToken data when skip == 0:
                    builder.Append(WebUtility.HtmlDecode(AnySpace().Replace(data.Data, " ")));
                    break;
            }
        }

        return Normalize(BlankRuns().Replace(builder.ToString(), "\n\n"));
    }

    /// <summary>
    /// The new text of a reply: everything above the first line that starts the quoted earlier
    /// email ("On … wrote:", Arabic "كتب", Outlook's "From:" block or "-----Original Message-----",
    /// lines starting with "&gt;"). Returns the whole text when nothing is left above it.
    /// </summary>
    public static string StripQuoted(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = Normalize(text).Split('\n');
        var cut = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('>') && QuoteFollows(lines, i)
                || OriginalMessage().IsMatch(line)
                || WroteLine().IsMatch(line)
                || (i + 1 < lines.Length && WroteLine().IsMatch(line + " " + lines[i + 1].Trim()) && line.Length > 0)
                || OutlookHeader().IsMatch(line) && i + 1 < lines.Length && OutlookNext().IsMatch(lines[i + 1].Trim()))
            {
                cut = i;
                break;
            }
        }

        var kept = string.Join('\n', lines.Take(cut)).Trim();
        return kept.Length > 0 ? kept : Normalize(text).Trim();
    }

    /// <summary>Message-ID without angle brackets and white space; null when empty or too long.</summary>
    public static string? Clean(string? messageId)
    {
        var value = messageId?.Trim().Trim('<', '>').Trim();
        return value is { Length: > 0 and <= Data.Tickets.TicketComment.MessageIdMaxLength } && !value.Any(char.IsWhiteSpace) ? value : null;
    }

    /// <summary>Whether most letters of <paramref name="text"/> are Arabic.</summary>
    public static bool LooksArabic(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int arabic = 0, latin = 0;
        foreach (var c in text)
        {
            if (c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿')
            {
                arabic++;
            }
            else if (char.IsAsciiLetter(c))
            {
                latin++;
            }
        }

        return arabic > latin;
    }

    private static List<EmailFile> Files(MimeMessage message)
    {
        var files = new List<EmailFile>();
        foreach (var part in message.Attachments.OfType<MimePart>().Concat(message.BodyParts.OfType<MimePart>().Where(p => p.IsAttachment)).Distinct())
        {
            if (files.Count >= MaxFiles)
            {
                break;
            }

            // Inline images of signatures are not attachments a person sent.
            if (part.ContentDisposition?.Disposition == ContentDisposition.Inline && part.ContentId is not null)
            {
                continue;
            }

            using var stream = new MemoryStream();
            part.Content?.DecodeTo(stream);
            var name = part.FileName is { Length: > 0 } n ? n : "attachment";
            files.Add(new EmailFile(name, stream.ToArray()));
        }

        foreach (var attached in message.Attachments.OfType<MessagePart>())
        {
            if (files.Count >= MaxFiles || attached.Message is null)
            {
                break;
            }

            using var stream = new MemoryStream();
            attached.Message.WriteTo(stream);
            var name = (attached.ContentDisposition?.FileName ?? attached.Message.Subject ?? "message") is { Length: > 0 } n ? n : "message";
            files.Add(new EmailFile(name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.eml", stream.ToArray()));
        }

        return files;
    }

    private static bool QuoteFollows(string[] lines, int index)
    {
        // A quote block runs to the end, apart from blank lines and more quoted lines.
        for (var j = index; j < lines.Length; j++)
        {
            var line = lines[j].Trim();
            if (line.Length > 0 && !line.StartsWith('>'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace(" ", " ", StringComparison.Ordinal);

    private static string OneLine(string text) => Whitespace().Replace(ControlCharacters().Replace(text, " "), " ").Trim();

    [GeneratedRegex(@"\bdmarc\s*=\s*fail\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DmarcFail();

    [GeneratedRegex(@"^-{2,}\s*(Original Message|Forwarded message|الرسالة الأصلية)\s*-{2,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OriginalMessage();

    [GeneratedRegex(@"^(On\s.{4,200}\swrote:|في\s.{4,200}(كتب|كتبت)\s?.{0,100}:|.{4,200}\s(كتب|كتبت):)$", RegexOptions.CultureInvariant)]
    private static partial Regex WroteLine();

    [GeneratedRegex(@"^(\*)?(From|من)\s?:(\*)?\s.+", RegexOptions.CultureInvariant)]
    private static partial Regex OutlookHeader();

    [GeneratedRegex(@"^(\*)?(Sent|Date|To|تاريخ الإرسال|أرسل|التاريخ|إلى)\s?:(\*)?\s.+", RegexOptions.CultureInvariant)]
    private static partial Regex OutlookNext();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s+")]
    private static partial Regex AnySpace();

    [GeneratedRegex(@"\n[ \t]*(\n[ \t]*)+")]
    private static partial Regex BlankRuns();

    [GeneratedRegex(@"\p{Cc}")]
    private static partial Regex ControlCharacters();
}
