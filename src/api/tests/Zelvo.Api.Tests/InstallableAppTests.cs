using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Branding;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Tickets.Branding;

namespace Zelvo.Api.Tests;

/// <summary>The installable web app (module 19, task J1): app icon sizes made by the server, and offline data.</summary>
public sealed class InstallableAppTests(DatabaseFixture database) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiFactory Factory => _factoryLazy ??= ApiFactory.ForDatabase(database).WithMasterKey();

    private ApiFactory? _factoryLazy;

    public ValueTask DisposeAsync() => _factoryLazy?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <summary>A square picture: an opaque red disc on transparency, so resizing and backgrounds can be checked.</summary>
    private static PngImage Disc(int size, bool opaqueBackground = false)
    {
        var image = new PngImage(size, size);
        var centre = size / 2.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var o = (y * size + x) * 4;
                var inside = Math.Pow(x + 0.5 - centre, 2) + Math.Pow(y + 0.5 - centre, 2) <= Math.Pow(size * 0.3, 2);
                if (inside)
                {
                    image.Pixels[o] = 200;
                    image.Pixels[o + 3] = 255;
                }
                else if (opaqueBackground)
                {
                    image.Pixels[o] = image.Pixels[o + 1] = 20;
                    image.Pixels[o + 2] = 90;
                    image.Pixels[o + 3] = 255;
                }
            }
        }

        return image;
    }

    private static byte[] Chunk(string type, byte[] body)
    {
        var bytes = new byte[12 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, body.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        body.CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8 + body.Length), Crc(bytes.AsSpan(4, 4 + body.Length)));
        return bytes;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xffffffffu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            }
        }

        return c ^ 0xffffffffu;
    }

    // A 4 × 1 palette PNG at 2 bits per pixel with a transparent entry, built by hand.
    private static byte[] PalettePng()
    {
        byte[] header = [0, 0, 0, 4, 0, 0, 0, 1, 2, 3, 0, 0, 0];
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9];
        byte[] transparency = [255, 255, 255, 0];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write([0, 0b00_01_10_11]);
        }

        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", header), .. Chunk("PLTE", palette), .. Chunk("tRNS", transparency),
            .. Chunk("IDAT", compressed.ToArray()), .. Chunk("IEND", [])];
    }

    [Fact]
    public void Png_round_trips_and_reads_palettes_with_transparency()
    {
        var disc = Disc(64);
        var decoded = PngImage.Decode(disc.Encode());
        Assert.Equal(64, decoded.Width);
        Assert.Equal(disc.Pixels, decoded.Pixels);

        var palette = PngImage.Decode(PalettePng());
        Assert.Equal(4, palette.Width);
        Assert.Equal([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 9, 9, 9, 0], palette.Pixels);
    }

    [Fact]
    public void Png_reader_refuses_damaged_interlaced_and_huge_files()
    {
        var png = Disc(16).Encode();
        png[^20] ^= 0xff;
        Assert.Equal("png-unreadable", Assert.Throws<PngFormatException>(() => PngImage.Decode(png)).Code);
        Assert.Equal("png-unreadable", Assert.Throws<PngFormatException>(() => PngImage.Decode([1, 2, 3])).Code);

        byte[] interlaced = [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 1];
        Assert.Equal("png-interlaced", Assert.Throws<PngFormatException>(() =>
            PngImage.Decode([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", interlaced), .. Chunk("IEND", [])])).Code);
        byte[] huge = [0, 0, 0x40, 0x01, 0, 0, 0x40, 0x01, 8, 6, 0, 0, 0];
        Assert.Equal("png-too-large", Assert.Throws<PngFormatException>(() =>
            PngImage.Decode([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", huge), .. Chunk("IEND", [])])).Code);
    }

    [Fact]
    public void App_icons_are_checked_and_resized_into_every_size_phones_need()
    {
        Assert.Equal("app-icon-too-small", AppIcons.Check(Disc(256).Encode())?.Code);
        var wide = new PngImage(600, 512).Encode();
        Assert.Equal("app-icon-not-square", AppIcons.Check(wide)?.Code);
        var icon = Disc(512).Encode();
        Assert.Null(AppIcons.Check(icon));

        foreach (var rendition in AppIcons.Renditions)
        {
            var image = PngImage.Decode(AppIcons.Render(icon, rendition, (255, 255, 255)));
            Assert.Equal(rendition.Size, image.Width);
            Assert.Equal(rendition.Size, image.Height);
            var corner = image.Pixels.AsSpan(0, 4).ToArray();
            var centre = (rendition.Size / 2 * rendition.Size + rendition.Size / 2) * 4;
            Assert.Equal(200, image.Pixels[centre]);
            if (rendition.Purpose == AppIconPurpose.Any)
            {
                // Transparency kept.
                Assert.Equal(0, corner[3]);
            }
            else
            {
                // A full background (white here), as masks and iOS need.
                Assert.Equal<byte[]>([255, 255, 255, 255], corner);
            }
        }

        // Maskable: the disc (60 % of the icon) shrinks into the safe zone.
        var maskable = PngImage.Decode(AppIcons.Render(icon, AppIcons.Find("maskable-512")!, (255, 255, 255)));
        var edgeOfDisc = (256 * 512 + (int)(256 + 512 * 0.3 * AppIcons.MaskableShare) + 4) * 4;
        Assert.Equal(255, maskable.Pixels[edgeOfDisc + 1]);

        // An icon with its own opaque background keeps it.
        var own = PngImage.Decode(AppIcons.Render(Disc(512, opaqueBackground: true).Encode(), AppIcons.Find("apple-180")!, (255, 255, 255)));
        Assert.Equal<byte[]>([20, 20, 90, 255], own.Pixels.AsSpan(0, 4).ToArray());
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] content, string fileName = "icon.png")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        form.Add(file, "file", fileName);
        return await client.PostAsync(new Uri("/api/v1/settings/branding/images/AppIcon", UriKind.Relative), form, Ct);
    }

    [Fact]
    public async Task The_published_app_icon_is_served_in_every_size_and_offline_data_is_a_tenant_setting()
    {
        var tenant = await database.CreateTenantAsync("j1app");
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var visitor = Factory.ForTenant(tenant);

        // Zelvo's own icon and offline data on until the tenant changes them.
        var before = await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/app");
        Assert.True((bool)before["offlineData"]!);
        Assert.Null(before["icons"]);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(new Uri("/api/v1/branding/app-icons/any-192", UriKind.Relative), Ct)).StatusCode);

        // Square PNGs of at least 512 pixels only.
        Assert.Equal("app-icon-too-small", (await (await UploadAsync(admin, Disc(128).Encode())).ProblemAsync()).Code);
        Assert.Equal("file-type-not-allowed", (await (await UploadAsync(admin, Disc(512).Encode(), "icon.webp")).ProblemAsync()).Code);
        using (var uploaded = await UploadAsync(admin, Disc(512).Encode()))
        {
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        }

        await using (var db = TestData.AppContext(database, tenant))
        {
            await db.TicketAttachments.Where(a => a.BrandingSlot == BrandingSlot.AppIcon).ExecuteUpdateAsync(set => set.SetProperty(a => a.ScanStatus, AttachmentScanStatus.Clean), Ct);
        }

        // Not served before it is published.
        Assert.Null((await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/app"))["icons"]);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/settings/branding/publish");
        var icons = (await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/app"))["icons"]!.AsObject();
        Assert.Equal(AppIcons.Renditions.Select(r => r.Name).Order(), icons.Select(i => i.Key).Order());
        foreach (var rendition in AppIcons.Renditions)
        {
            using var response = await visitor.GetAsync(new Uri((string)icons[rendition.Name]!, UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            var image = PngImage.Decode(await response.Content.ReadAsByteArrayAsync(Ct));
            Assert.Equal(rendition.Size, image.Width);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(new Uri("/api/v1/branding/app-icons/any-1024", UriKind.Relative), Ct)).StatusCode);

        // Offline data: branding.manage only, audited.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Put, "/api/v1/settings/app", new { offlineData = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/app", new { })).StatusCode);
        var changed = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/app", new { offlineData = false });
        Assert.False((bool)changed["offlineData"]!);
        Assert.False((bool)(await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/app"))["offlineData"]!);
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "app.settings.changed");
    }
}
