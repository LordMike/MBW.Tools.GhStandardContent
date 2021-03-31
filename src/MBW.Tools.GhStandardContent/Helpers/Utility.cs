using System;

namespace MBW.Tools.GhStandardContent.Helpers
{
    static class Utility
    {
        public static void NormalizeNewlines(ref byte[] data)
        {
            int length = data.Length;
            for (int i = 1; i < length; i++)
            {
                if (data[i - 1] != '\r' || data[i] != '\n')
                    continue;

                // Remove '\r'
                Array.Copy(data, i, data, i - 1, length - i);
                length--;
            }

            Array.Resize(ref data, length);
        }
    }
}