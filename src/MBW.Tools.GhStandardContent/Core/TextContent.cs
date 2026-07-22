using System.Text;

namespace MBW.Tools.GhStandardContent.Core;

internal static class TextContent
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Normalize(byte[] input)
    {
        if (!IsUtf8Text(input))
            return input;

        if (input.AsSpan().IndexOf("\r\n"u8) < 0)
            return input;

        using MemoryStream output = new(input.Length);
        for (int index = 0; index < input.Length; index++)
        {
            if (input[index] == (byte)'\r' && index + 1 < input.Length && input[index + 1] == (byte)'\n')
                continue;
            output.WriteByte(input[index]);
        }

        return output.ToArray();
    }

    public static bool IsUtf8Text(byte[] input)
    {
        if (input.Contains((byte)0))
            return false;

        try
        {
            _ = StrictUtf8.GetString(input);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static byte[] Append(byte[] standard, byte[] local)
    {
        if (!IsUtf8Text(standard) || !IsUtf8Text(local))
            throw new InvalidOperationException("Local overrides can only be appended to UTF-8 text content.");

        standard = Normalize(standard);
        local = Normalize(local);
        bool separatorNeeded = standard.Length > 0 && standard[^1] != (byte)'\n';
        byte[] merged = new byte[standard.Length + (separatorNeeded ? 1 : 0) + local.Length];
        Buffer.BlockCopy(standard, 0, merged, 0, standard.Length);
        int offset = standard.Length;
        if (separatorNeeded)
            merged[offset++] = (byte)'\n';
        Buffer.BlockCopy(local, 0, merged, offset, local.Length);
        return merged;
    }
}
