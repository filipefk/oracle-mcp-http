using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace GeraApiKey;

// Junta uma lista de strings numa única API Key segura para um header HTTP (só A-Z, a-z, 0-9, '-' e '_')
// e desfaz a key devolvendo os valores na mesma ordem. É ofuscação, não criptografia: não há segredo,
// quem conhece o algoritmo consegue reverter — trate a key gerada como um segredo.
public static class ApiKeyGenerator
{
    // Unit Separator (U+001F): caractere de controle, não digitável, que separa os valores antes do embaralhamento.
    public const char Separator = (char)0x1F;

    private const uint KeystreamBase = 0x9E3779B9;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Generate(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var list = values.ToList();
        if (list.Count == 0)
            throw new ArgumentException("Informe ao menos um valor.", nameof(values));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is null)
                throw new ArgumentException($"O valor na posição {i} é nulo.", nameof(values));
            if (list[i].Contains(Separator))
                throw new ArgumentException($"O valor na posição {i} contém o caractere separador (U+001F).", nameof(values));
        }

        var plain = StrictUtf8.GetBytes(string.Join(Separator, list));

        // Primeiro byte = semente aleatória: a mesma lista gera keys diferentes e prefixos comuns
        // (ex: "https://dev.azure.com/") não ficam reconhecíveis na key.
        var buffer = new byte[plain.Length + 1];
        buffer[0] = (byte)RandomNumberGenerator.GetInt32(256);
        plain.CopyTo(buffer, 1);
        Scramble(buffer.AsSpan(1), buffer[0]);

        return Base64Url.EncodeToString(buffer);
    }

    public static IReadOnlyList<string> Parse(string apiKey) =>
        TryParse(apiKey, out var values) ? values : throw new FormatException("API Key inválida.");

    public static bool TryParse(string? apiKey, out IReadOnlyList<string> values)
    {
        values = [];
        if (string.IsNullOrWhiteSpace(apiKey)) return false;

        byte[] buffer;
        try
        {
            buffer = Base64Url.DecodeFromChars(apiKey.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        if (buffer.Length == 0) return false;

        Scramble(buffer.AsSpan(1), buffer[0]);

        try
        {
            values = StrictUtf8.GetString(buffer, 1, buffer.Length - 1).Split(Separator);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        return true;
    }

    // XOR com um keystream xorshift32 derivado da semente — aplicar de novo com a mesma semente desfaz.
    private static void Scramble(Span<byte> data, byte seed)
    {
        // Nunca zera (o xorshift travaria em zero): KeystreamBase não é um byte repetido 4 vezes.
        var state = KeystreamBase ^ (seed * 0x01010101u);
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] ^= (byte)(state >> 24);
        }
    }
}
