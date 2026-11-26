using System.Text;
using MimeKit;
using Zelvo.Api.Integrations.Bulk;
using Zelvo.Licensing;
using Zelvo.Notifications;

namespace Zelvo.Api.Tests;

/// <summary>The hidden watermark in generated files (module 12, task F6).</summary>
public sealed class InstallationMarkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_mark_hides_the_watermark_and_decodes_back()
    {
        var watermark = Watermark.Create();

        var mark = InstallationMark.Encode(watermark);

        Assert.NotNull(mark);
        Assert.DoesNotContain("ZW", mark, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(watermark, InstallationMark.Decode(mark));
        Assert.Null(InstallationMark.Encode(null));
        Assert.Null(InstallationMark.Decode("not a mark"));
        Assert.Null(InstallationMark.Decode(Convert.ToHexStringLower(Encoding.ASCII.GetBytes("something else"))));
    }

    [Fact]
    public void Pdfs_carry_the_mark_before_their_end_without_moving_the_cross_reference()
    {
        var watermark = Watermark.Create();
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\nxref\n0 1\ntrailer\n<<>>\nstartxref\n9\n%%EOF\n");

        var marked = InstallationMark.MarkPdf(pdf, watermark);

        var text = Encoding.ASCII.GetString(marked);
        Assert.StartsWith(Encoding.ASCII.GetString(pdf)[..^"%%EOF\n".Length], text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(watermark, text, StringComparison.Ordinal);
        Assert.Equal(watermark, InstallationMark.ReadPdf(marked));
        Assert.Same(pdf, InstallationMark.MarkPdf(pdf, null));
        Assert.Null(InstallationMark.ReadPdf(pdf));
    }

    [Fact]
    public async Task Excel_exports_carry_the_mark_and_the_signing_tool_reads_it()
    {
        var watermark = Watermark.Create();
        var workbook = TabularFile.Write(TabularFormat.Xlsx, ["name"], [["زيلفو"]], watermark);
        var file = Path.Combine(Path.GetTempPath(), $"zelvo-mark-{Guid.NewGuid():N}.xlsx");
        await File.WriteAllBytesAsync(file, workbook, Ct);
        try
        {
            Assert.Equal(watermark, await MarkAsync(file));
            Assert.Equal(["name", "زيلفو"], TabularFile.Read(TabularFormat.Xlsx, new MemoryStream(workbook), 10).Select(r => r[0]));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Emails_carry_the_mark_in_a_header()
    {
        var watermark = Watermark.Create();
        var message = new EmailMessage("someone@example.org", "Municipality", "Subject", "<p>Hello</p>", "Hello", "en", "id@entity.example");

        using var mime = SmtpEmailSender.Build(message, "zelvo@installation.example", watermark);
        using var unmarked = SmtpEmailSender.Build(message, "zelvo@installation.example");

        Assert.Equal(watermark, InstallationMark.Decode(mime.Headers[InstallationMark.EmailHeader]));
        Assert.Null(unmarked.Headers[InstallationMark.EmailHeader]);
        var file = Path.Combine(Path.GetTempPath(), $"zelvo-mark-{Guid.NewGuid():N}.eml");
        await mime.WriteToAsync(file, Ct);
        try
        {
            Assert.Equal(watermark, await MarkAsync(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<string> MarkAsync(string file)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await SigningTool.SigningTool.RunAsync(["mark", file], output, error, _ => null);
        Assert.True(code == 0, error.ToString());
        return output.ToString().Trim();
    }
}
