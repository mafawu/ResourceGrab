using ResourceGrab.Core.Logging;
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
}
