// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class TextFileEncoding
{
    public const string Utf8 = "utf-8";
    public const string Utf8Bom = "utf-8-bom";
    public const string Utf16Le = "utf-16le";
    public const string Utf16LeBom = "utf-16le-bom";
    public const string Utf16Be = "utf-16be";
    public const string Utf16BeBom = "utf-16be-bom";
    public const string Utf32Le = "utf-32le";
    public const string Utf32LeBom = "utf-32le-bom";
    public const string Utf32Be = "utf-32be";
    public const string Utf32BeBom = "utf-32be-bom";

    private static readonly Encoding Utf8NoBomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf8BomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: false);
    private static readonly Encoding Utf32BeEncoding = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: false);
    private static readonly Encoding Utf32BeBomEncoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: false);

    public static DecodedText Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                return DecodeKnownEncoding(bytes[4..], Encoding.UTF32, Utf32LeBom, codeUnitSize: 4);
            }

            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            {
                return DecodeKnownEncoding(bytes[4..], Utf32BeEncoding, Utf32BeBom, codeUnitSize: 4);
            }
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return DecodeKnownEncoding(bytes[3..], Utf8NoBomEncoding, Utf8Bom);
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return DecodeKnownEncoding(bytes[2..], Encoding.Unicode, Utf16LeBom, codeUnitSize: 2);
            }

            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return DecodeKnownEncoding(bytes[2..], Encoding.BigEndianUnicode, Utf16BeBom, codeUnitSize: 2);
            }
        }

        if (LooksLikeUtf16(bytes, littleEndian: true))
        {
            return DecodeKnownEncoding(bytes, Encoding.Unicode, Utf16Le, codeUnitSize: 2);
        }

        if (LooksLikeUtf16(bytes, littleEndian: false))
        {
            return DecodeKnownEncoding(bytes, Encoding.BigEndianUnicode, Utf16Be, codeUnitSize: 2);
        }

        return DecodeKnownEncoding(bytes, Utf8NoBomEncoding, Utf8);
    }

    public static byte[] Encode(string content, string? encodingName)
    {
        return NormalizeEncodingName(encodingName) switch
        {
            Utf8Bom => EncodeWithPreamble(content, Utf8BomEncoding),
            Utf16Le => Encoding.Unicode.GetBytes(content),
            Utf16LeBom => EncodeWithPreamble(content, Encoding.Unicode),
            Utf16Be => Encoding.BigEndianUnicode.GetBytes(content),
            Utf16BeBom => EncodeWithPreamble(content, Encoding.BigEndianUnicode),
            Utf32Le => Encoding.UTF32.GetBytes(content),
            Utf32LeBom => EncodeWithPreamble(content, Encoding.UTF32),
            Utf32Be => Utf32BeEncoding.GetBytes(content),
            Utf32BeBom => EncodeWithPreamble(content, Utf32BeBomEncoding),
            _ => Utf8NoBomEncoding.GetBytes(content)
        };
    }

    private static DecodedText DecodeKnownEncoding(
        ReadOnlySpan<byte> bytes,
        Encoding encoding,
        string encodingName,
        int codeUnitSize = 1)
    {
        var alignedBytes = codeUnitSize <= 1 ? bytes : TrimToCodeUnitBoundary(bytes, codeUnitSize);
        return new DecodedText(encoding.GetString(alignedBytes), encodingName);
    }

    private static ReadOnlySpan<byte> TrimToCodeUnitBoundary(ReadOnlySpan<byte> bytes, int codeUnitSize)
    {
        var remainder = bytes.Length % codeUnitSize;
        return remainder == 0 ? bytes : bytes[..^remainder];
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, bool littleEndian)
    {
        var pairs = bytes.Length / 2;
        if (pairs < 4)
        {
            return false;
        }

        var zeroInFirstByte = 0;
        var zeroInSecondByte = 0;
        for (var index = 0; index < pairs; index++)
        {
            if (bytes[index * 2] == 0)
            {
                zeroInFirstByte++;
            }

            if (bytes[(index * 2) + 1] == 0)
            {
                zeroInSecondByte++;
            }
        }

        var requiredZeroes = Math.Max(2, pairs / 2);
        var allowedOppositeZeroes = Math.Max(1, pairs / 8);

        return littleEndian
            ? zeroInSecondByte >= requiredZeroes && zeroInFirstByte <= allowedOppositeZeroes
            : zeroInFirstByte >= requiredZeroes && zeroInSecondByte <= allowedOppositeZeroes;
    }

    private static byte[] EncodeWithPreamble(string content, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var contentBytes = encoding.GetBytes(content);
        if (preamble.Length == 0)
        {
            return contentBytes;
        }

        var bytes = new byte[preamble.Length + contentBytes.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(contentBytes, 0, bytes, preamble.Length, contentBytes.Length);
        return bytes;
    }

    private static string NormalizeEncodingName(string? encodingName) =>
        string.IsNullOrWhiteSpace(encodingName)
            ? Utf8
            : encodingName.Trim().ToLowerInvariant();
}

internal sealed record DecodedText(string Content, string EncodingName);
