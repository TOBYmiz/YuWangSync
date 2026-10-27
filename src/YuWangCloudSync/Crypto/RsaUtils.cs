using System.Numerics;
using System.Text;

namespace YuWangCloudSync.Crypto;

/// <summary>
/// ohdave RSAUtils.encryptedString 的 C# 等价实现。
/// 算法：无填充、小端分块、块间空格分隔。
/// 与 Python login.py 的 rsa_encrypt() 100% 一致（已验证）。
/// 参考：security.js (服务器原始 RSA.js, David Shapiro 1998-2005, Fuchun 2010 修改版)
/// </summary>
public static class RsaUtils
{
    /// <summary>1024-bit RSA 公钥模数（从育网云 welcome.jsx 前端硬编码提取）</summary>
    public const string ModulusHex =
        "861ef1da51e3f4148c16adee8931c6cef71e12701395bec1185bad621230ec7c" +
        "ed81d7d7072de116c81fa1bf46891745d941fc933684fbfda45834fb4702cc44" +
        "e1bc0dc4cec146efd950fa02b87856427adf8056b24d40702e6d07f488242172" +
        "e0fa37023f14b7337bf73f90cad3510f835dc168ab903e146d00286986193f9f";

    /// <summary>公钥指数</summary>
    public const string ExponentHex = "10001";

    // chunkSize = 2 * biHighIndex(modulus)
    // modulus 1024 bit = 64 个 16-bit digit → biHighIndex = 63 → chunkSize = 126
    private const int ChunkSize = 126;

    private static readonly BigInteger Modulus = ParseHexUnsigned(ModulusHex);
    private static readonly BigInteger Exponent = ParseHexUnsigned(ExponentHex);

    /// <summary>
    /// 复刻 RSAUtils.encryptedString(key, s)。
    /// 输入：原始字符串（调用方负责对用户名先做 Uri.EscapeDataString）。
    /// 输出：空格分隔的 hex 块序列（与浏览器前端输出格式一致）。
    /// </summary>
    public static string Encrypt(string s)
    {
        // 1. 每字符 charCode & 0xFF
        var a = new List<byte>(s.Length);
        foreach (char c in s) a.Add((byte)(c & 0xFF));

        // 2. 补零至 chunkSize 的倍数（Rob Saunders 修改版：补在数组末尾）
        while (a.Count % ChunkSize != 0) a.Add(0);

        // 3. 每块（chunkSize 字节）按 ohdave 小端组装为大整数 → pow(v, e, n) → hex
        var parts = new List<string>(a.Count / ChunkSize);
        for (int start = 0; start < a.Count; start += ChunkSize)
        {
            BigInteger v = BigInteger.Zero;
            // 每 2 字节组成一个 16-bit digit：digit[j] = a[2j] + a[2j+1]<<8
            // 整数值 = Σ digit[j] << (16 * j)   等价于 v += (a[2j] + a[2j+1]<<8) << (8 * 2j) 即 16*j 位
            for (int j = 0; j < ChunkSize; j += 2)
            {
                int digit = a[start + j] | (a[start + j + 1] << 8);
                v += (BigInteger)digit << (8 * j);  // 8 * j == 16 * (j/2)，但 j 以 2 步进
            }
            BigInteger cipher = BigInteger.ModPow(v, Exponent, Modulus);
            // biToHex 输出（不去前导零，hex 小写）
            parts.Add(cipher.ToString("x"));
        }
        return string.Join(" ", parts);
    }

    /// <summary>解析无符号十六进制大整数（大端 hex 字符串）</summary>
    private static BigInteger ParseHexUnsigned(string hex)
    {
        // BigInteger.Parse(hex, HexNumber) 接受大端 hex 字符串
        // 加前导 "0" 防止最高位为 1 的 hex（如 0x86...）被解释为负数
        return BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
    }
}
