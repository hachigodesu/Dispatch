using System.Buffers.Binary;
using System.Text;
using Dispatch.Core.Profiles;
using Dispatch.Core.Sii;
using Dispatch.Core.Sii.Binary;
using Dispatch.Core.Sii.Text;

namespace Dispatch.Core.Tests.Sii;

public class EncodedStringTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("user_profile")]
    [InlineData("dlc_north")]
    [InlineData("z9_0")]
    public void Encode_then_decode_round_trips(string token) =>
        EncodedString.Decode(EncodedString.Encode(token)).ShouldBe(token);

    [Fact]
    public void Decode_ignores_bit_63() =>
        EncodedString.Decode(EncodedString.Encode("scania") | 0x8000_0000_0000_0000UL).ShouldBe("scania");

    [Fact]
    public void First_character_is_least_significant_digit() =>
        EncodedString.Encode("1").ShouldBe(2UL); // '1' has index 2

    [Fact]
    public void Rejects_too_long_tokens() =>
        Should.Throw<ArgumentException>(() => EncodedString.Encode("abcdefghijklm"));
}

public class BsiiFormattingTests
{
    [Theory]
    [InlineData(0x27ac7a91bf8UL, "_nameless.27a.c7a9.1bf8")]
    [InlineData(0x1UL, "_nameless.1")]
    [InlineData(0x10000UL, "_nameless.1.0")]
    public void Nameless_ids_use_16_bit_hex_groups(ulong value, string expected) =>
        BsiiDecoder.FormatNameless(value).ShouldBe(expected);

    [Theory]
    [InlineData(0f, "0")]
    [InlineData(42f, "42")]
    [InlineData(-3f, "-3")]
    [InlineData(1.5f, "&3fc00000")]
    public void Floats_are_lossless(float value, string expected) =>
        BsiiDecoder.Float(value).ShouldBe(expected);
}

public class SiiStringTests
{
    private const string JapanEscaped = @"""\xe6\x97\xa5\xe6\x9c\xac""";

    [Theory]
    [InlineData("Promods", @"""Promods""")]
    [InlineData("日本", JapanEscaped)]
    [InlineData(@"a""b\c", @"""a\""b\\c""")]
    [InlineData("RPM - QuickStart|x", @"""RPM - QuickStart|x""")]
    public void Quote_matches_game_escaping(string value, string quoted)
    {
        SiiString.Quote(value).ShouldBe(quoted);
        SiiString.Unquote(quoted).ShouldBe(value);
    }

    [Fact]
    public void Unquote_tolerates_raw_utf8_and_emoji() =>
        SiiString.Unquote("\"日本 🚚\"").ShouldBe("日本 🚚");
}

public class BsiiDecoderTests
{
    /// <summary>Builds a tiny BSII file: one structure with a string, token, int and string array.</summary>
    private static byte[] BuildSample()
    {
        var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); ms.Write(b); }
        void Str(string s) { var bytes = Encoding.UTF8.GetBytes(s); U32((uint)bytes.Length); ms.Write(bytes); }

        U32(0x49495342); U32(2);                       // header
        U32(0); ms.WriteByte(1); U32(1); Str("user_profile");
        U32(0x01); Str("profile_name");
        U32(0x03); Str("brand");
        U32(0x25); Str("version");
        U32(0x02); Str("active_mods");
        U32(0);                                        // end of field list
        U32(1);                                        // data block for structure 1
        ms.WriteByte(0xFF); U64(0x27ac7a91bf8UL);      // nameless id
        Str("日本");
        U64(EncodedString.Encode("scania"));
        U32(6);
        U32(2); Str("promods-eu-def-v283|ProMods Definition"); Str("mod_workshop_package.00000000C3CC68CD|RPM - QuickStart");
        U32(0); ms.WriteByte(0);                       // end marker
        return ms.ToArray();
    }

    [Fact]
    public void Decodes_structure_and_data_blocks_to_text()
    {
        string text = BsiiDecoder.Decode(BuildSample(), out var info);

        info.Version.ShouldBe(2u);
        info.UnitCount.ShouldBe(1);
        info.TrailingBytes.ShouldBe(0);
        text.ShouldBe(string.Join("\r\n",
            "SiiNunit",
            "{",
            "user_profile : _nameless.27a.c7a9.1bf8 {",
            @" profile_name: ""\xe6\x97\xa5\xe6\x9c\xac""",
            " brand: scania",
            " version: 6",
            " active_mods: 2",
            @" active_mods[0]: ""promods-eu-def-v283|ProMods Definition""",
            @" active_mods[1]: ""mod_workshop_package.00000000C3CC68CD|RPM - QuickStart""",
            "}",
            "",
            "}",
            ""));
    }

    [Fact]
    public void Codec_detects_binary_layer() =>
        SiiFileCodec.Decode(BuildSample()).Layers.ShouldBe([SiiFormat.Binary]);

    [Fact]
    public void Truncated_data_throws_invalid_data() =>
        Should.Throw<InvalidDataException>(() => BsiiDecoder.Decode(BuildSample()[..^10]));
}

public class ProfileNameCodecTests
{
    [Theory]
    [InlineData("Promods", "50726F6D6F6473")]
    [InlineData("日本", "E697A5E69CAC")]
    [InlineData("名無し", "E5908DE784A1E38197")]
    public void Folder_name_is_uppercase_utf8_hex(string name, string folder)
    {
        ProfileNameCodec.ToFolderName(name).ShouldBe(folder);
        ProfileNameCodec.TryFromFolderName(folder.ToLowerInvariant(), out var back).ShouldBeTrue();
        back.ShouldBe(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("profiles(1.62.2.1s).bak")]
    [InlineData("FF")]
    public void Rejects_non_profile_folders(string folder) =>
        ProfileNameCodec.TryFromFolderName(folder, out _).ShouldBeFalse();
}
