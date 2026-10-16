using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Kestrel on 127.0.0.1 with a throwaway self-signed certificate. POST /r?s=<bytes> answers with a
// 100-byte body and pads its response headers so the header block is <bytes> long.
public static class TestServer
{
    public const int ResponseBodyBytes = 100;
    private static readonly byte[] ResponseBody = new byte[ResponseBodyBytes];

    public static async Task<WebApplication> StartAsync(int port, bool http3)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (http3)
        {
            builder.WebHost.UseQuic(); // the slim builder leaves the QUIC transport out
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = true;
            kestrel.Listen(IPAddress.Loopback, port, listen =>
            {
                listen.Protocols = http3 ? HttpProtocols.Http3 : HttpProtocols.Http1;
                listen.UseHttps(CreateCertificate());
            });
        });

        var app = builder.Build();
        app.MapPost("/r", async (HttpContext context) =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null);
            var target = int.Parse(context.Request.Query["s"]!);
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = ResponseBodyBytes;
            context.Response.Headers[HeaderSizer.PadName] = new string('a', ResponsePad(target));
            await context.Response.Body.WriteAsync(ResponseBody);
        });

        await app.StartAsync();
        return app;
    }

    // Kestrel's own response headers, as its QPACK writer sees them (date is always 29 characters).
    private static int ResponsePad(int targetBytes) => HeaderSizer.PadFor(
    [
        (":status", "200"),
        ("content-type", "application/octet-stream"),
        ("date", "Fri, 16 Oct 2026 09:00:00 GMT"),
        ("server", "Kestrel"),
        ("content-length", ResponseBodyBytes.ToString()),
    ], targetBytes);

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }
}
