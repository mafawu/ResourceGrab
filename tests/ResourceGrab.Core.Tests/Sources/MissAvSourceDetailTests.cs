using ResourceGrab.Core.Logging;
using ResourceGrab.Core.Sources;
using ResourceGrab.Core.Sources.VideoSources;
using Xunit;

namespace ResourceGrab.Core.Tests.Sources;

// MissAV 详情页信息行/磁力表格解析测试：夹具取自 missav.live 真实详情页结构。

public class MissAvSourceDetailTests
{
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine("Fixtures", "missav-detail.html"));

    [Fact]
    public void ParseInfoRows_MapsSynonymsToUnifiedFields()
    {
        var info = MissAvSource.ParseInfoRows(LoadFixture());

        Assert.Equal(["SSIS-971"], info["number"]);
        Assert.Equal(["2024-02-09"], info["release"]);
        Assert.Equal(["山手梨愛"], info["actress"]);
        Assert.Equal(["S1"], info["maker"]);
        Assert.Equal(["トレンディ山口"], info["director"]);
        Assert.Equal(["S1 NO.1 STYLE"], info["label"]);
        Assert.Equal(3, info["genres"].Count);
        Assert.Contains("巨乳", info["genres"]);
        Assert.StartsWith("アスリート級ボディ", info["title"][0]);
    }

    [Fact]
    public void ParseMagnets_ParsesRowsAndDedupesByBtih()
    {
        var magnets = MissAvSource.ParseMagnets(LoadFixture());

        Assert.Equal(2, magnets.Count);
        Assert.Equal("118877.xyz SSIS-971", magnets[0].Name);
        Assert.Equal("magnet:?xt=urn:btih:26888B4F91BFDF4507EAAAF87C20232EC7585182&size=6550815793&biz=ktr",
            magnets[0].Url);
        Assert.Equal("6.4GB", magnets[0].Size);
        Assert.Equal("2024-05-09", magnets[0].Date);
        Assert.Equal("SSIS-971.[4K]@RUNBKK", magnets[1].Name);
        Assert.Equal("20.51GB", magnets[1].Size);
    }

    [Fact]
    public void ParsePreviewImages_CollectsLazyAndProtocolRelative_StopsAtNextSection()
    {
        var images = MissAvSource.ParsePreviewImages(LoadFixture());

        // 三张預覽圖：直接 src、懒加载 data-src、协议相对 //；后续"同系列"干扰图不收
        Assert.Equal(3, images.Count);
        Assert.Equal("https://pics.dmm.co.jp/digital/video/ssis00971/ssis00971-1.jpg", images[0]);
        Assert.Equal("https://pics.dmm.co.jp/digital/video/ssis00971/ssis00971-2.jpg", images[1]);
        Assert.Equal("https://pics.dmm.co.jp/digital/video/ssis00971/ssis00971-3.jpg", images[2]);
        Assert.DoesNotContain("unrelated-thumb", string.Join(",", images));
    }

    [Fact]
    public void ParsePreviewImages_ReturnsEmptyWhenSectionMissing()
    {
        Assert.Empty(MissAvSource.ParsePreviewImages("<html><body><p>沒有預覽的頁面</p></body></html>"));
    }

    // 磁力名称单元格里紧跟链接的 <span> 角标（站点服务端渲染），必须跟名称一起取出来
    private const string MagnetBadgeHtml = """
        <html><body><table>
        <tr>
            <td class="py-4 w-full text-sm text-nord13 font-medium space-x-1">
                <a rel="nofollow" href="magnet:?xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&amp;dn=IPZZ-961-C" target="_blank">
                    IPZZ-961-C
                </a>
                <span class="bg-primary inline-flex items-center px-2 py-0.5 rounded text-xs font-medium text-white">
                    高清
                </span>
                <span class="bg-nord10 inline-flex items-center px-2 py-0.5 rounded text-xs font-medium text-white">
                    字幕
                </span>
            </td>
            <td class="whitespace-nowrap pl-4 text-right text-sm text-gray-400 font-mono">7.68GB</td>
            <td class="hidden sm:table-cell whitespace-nowrap pl-4 text-right text-sm text-gray-400 font-mono">2026-10-13</td>
            <td class="whitespace-nowrap pl-4 text-right"><a href="https://keepshare.org/x/magnet:?xt=urn:btih:AAAA">下載</a></td>
        </tr>
        <tr>
            <td class="py-4 w-full text-sm text-nord13 font-medium space-x-1">
                <a rel="nofollow" href="magnet:?xt=urn:btih:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB&amp;dn=IPZZ-961-UC" target="_blank">
                    IPZZ-961-UC
                </a>
                <span class="bg-primary inline-flex items-center px-2 py-0.5 rounded text-xs font-medium text-white">
                    高清
                </span>
            </td>
            <td class="whitespace-nowrap pl-4 text-right text-sm text-gray-400 font-mono">5.14GB</td>
            <td class="hidden sm:table-cell whitespace-nowrap pl-4 text-right text-sm text-gray-400 font-mono">2026-10-20</td>
        </tr>
        </table></body></html>
        """;

    [Fact]
    public void ParseMagnets_CapturesBadgeTagsFromNameCell()
    {
        var magnets = MissAvSource.ParseMagnets(MagnetBadgeHtml);

        Assert.Equal(2, magnets.Count);

        Assert.Equal("IPZZ-961-C", magnets[0].Name);
        Assert.Equal("7.68GB", magnets[0].Size);
        Assert.Equal("2026-10-13", magnets[0].Date);
        Assert.Contains("高清", magnets[0].Tags);
        Assert.Contains("字幕", magnets[0].Tags);
        Assert.True(magnets[0].HasHd);
        Assert.True(magnets[0].HasChineseSubtitle);

        // -UC 只有「高清」角标：不能因为番号带 C 就当字幕（已由角标判定优先）
        Assert.Equal("IPZZ-961-UC", magnets[1].Name);
        Assert.Contains("高清", magnets[1].Tags);
        Assert.False(magnets[1].HasChineseSubtitle);
    }

    [Theory]
    [InlineData("IPZZ-961-C", true)]
    [InlineData("ipzz-961ch", true)]
    [InlineData("IPZZ-961-CHS", true)]
    [InlineData("IPZZ-961 中文字幕 完全版", true)]
    [InlineData("IPZZ-961", false)]
    [InlineData("IPZZ-961-UC", false)]
    [InlineData("IPZZ-961-U", false)]
    [InlineData("IPZZ-961-AI", false)]
    [InlineData("118877.xyz SSIS-971", false)]
    public void MergeFromName_InfersSubtitleOnlyFromKnownSuffixes(string name, bool expected)
    {
        var tags = MagnetTagHelper.MergeFromName(Array.Empty<string>(), name);

        Assert.Equal(expected, tags.Contains("字幕"));
    }

    [Fact]
    public void MergeFromName_KeepsExplicitBadgesAndDoesNotDuplicate()
    {
        var tags = MagnetTagHelper.MergeFromName(["字幕", "高清"], "IPZZ-961-C");

        Assert.Equal(2, tags.Count);
        Assert.Contains("字幕", tags);
        Assert.Contains("高清", tags);
    }
}
