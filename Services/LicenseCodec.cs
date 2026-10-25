using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrtakAlan;

public readonly record struct LicenseInfo(string Name, int Seats);

public static class LicenseCodec
{
    public const string Contact = "LvntYxl@gmail.com";

    static readonly byte[] PublicKey = Convert.FromBase64String(
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5hyhr+P2+Z/oKDffo5Aafi4+dSlGtNR2i2jzoU1THFlj7bhqIF6PjdzIQ6Yo4+sfE3RmfjqtX01LDcyMbLlpOA==");

    public static string Issue(string name, int seats, byte[] privateKey)
    {
        var data = Payload(name, seats);
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(privateKey, out _);
        var signature = ec.SignData(data, HashAlgorithmName.SHA256);
        return "OA1." + Base64(data) + "." + Base64(signature);
    }

    public static bool TryRead(string key, out LicenseInfo info)
    {
        info = default;
        key = string.Concat(key.Where(ch => !char.IsWhiteSpace(ch)));
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0] != "OA1") return false;
        byte[] data;
        byte[] signature;
        try
        {
            data = Base64Decode(parts[1]);
            signature = Base64Decode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(PublicKey, out _);
        if (!ec.VerifyData(data, signature, HashAlgorithmName.SHA256)) return false;

        try
        {
            var payload = JsonSerializer.Deserialize<PayloadDto>(data);
            if (payload == null || payload.v != 1 || payload.s < 0) return false;
            var name = payload.n ?? "";
            if (name.Length > 80) name = name[..80];
            info = new LicenseInfo(name, payload.s);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static byte[] Payload(string name, int seats)
    {
        name = name.Trim();
        if (name.Length > 80) name = name[..80];
        if (seats < 0) seats = 0;
        return JsonSerializer.SerializeToUtf8Bytes(new PayloadDto(1, name, seats));
    }

    static string Base64(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] Base64Decode(string text)
    {
        var value = text.Replace('-', '+').Replace('_', '/');
        var pad = value.Length % 4;
        if (pad == 2) value += "==";
        else if (pad == 3) value += "=";
        else if (pad != 0) throw new FormatException();
        return Convert.FromBase64String(value);
    }

    sealed record PayloadDto(int v, string n, int s);
}
