using ResourceGrab.Core;
using ResourceGrab.Core.Services;
using ResourceGrab.Core.Sources.Alicesw;
using ResourceGrab.Core.Sources.Novel;
using Xunit;

namespace ResourceGrab.Core.Tests;

/// <summary>
/// 在线小说源的解析与本地优先匹配。
/// 全部用自造 HTML/文本，不依赖网络与站点真实内容。
/// </summary>
public class AliceswNovelTests
{
    private const string SearchHtml = """
        <html><body>
        <div class="list-group-item">
          <h5><a href="/novel/101.html">1. 山海旧事</a><small class="text-muted ms-2">[已完结]</small></h5>
          <p class="mb-1 text-muted">作者：<a href="/search.html?q=a&amp;f=author">陈砚</a> 字数：42.80万　浏览：87509</p>
          <p class="content-txt">山雨来的那夜，旧祠里多了一枚断碑。</p>
          <p class="text-muted">标签：<a href="/search.html?q=t&amp;f=tag"><span>#</span>玄幻</a> <a href="/search.html?q=t&amp;f=tag"><span>#</span>群像</a></p>
          <p class="timedesc">发布时间：2025-06-07 14:36 更新时间：2025-06-08 08:56</p>
        </div>
        <div class="list-group-item">
          <h5><a href="/novel/102.html">2. 长夜将明</a></h5>
          <p class="mb-1 text-muted">作者：<a href="/search.html?q=b&amp;f=author">苏未晚</a> 字数：31.20万字</p>
          <p class="content-txt">城里最后一盏灯灭了。</p>
          <p class="timedesc">更新时间：2025-07-01 10:00</p>
        </div>
        <div class='layui-box layui-laypage lang="en"'><a data-page='1'>1</a><a data-page='2'>2</a><a data-page='7'>7</a></div>
        </body></html>
        """;

    private const string DetailHtml = """
        <html><body>
        <div class="novel_title">山海旧事</div>
        <img class="lazyload_book_cover" data-src="https://img.example.com/c1.webp" src="/placeholder.webp" />
        <div class="novel_info">
          <p>作 者：<a href="/search.html?q=a&amp;f=author">陈砚</a></p>
          <p>分 类：<a href="/lists/62.html">玄幻</a></p>
          <p>热 度：87618 · 收 藏：923</p>
          <p>字 数：42.80万 · 章 节：3</p>
          <p>状 态：已完结</p>
          <p>最 新：<a href="/book/9001/ccc.html">第三章 断碑</a></p>
        </div>
        <div class="tags_list">
          标签：<a href="/search.html?q=t&amp;f=tag"><em>#</em>玄幻</a><a href="/search.html?q=t&amp;f=tag"><em>#</em>群像</a>
          <p>注意：该小说包含以上标签，请谨慎阅读</p>
        </div>
        <div class="jianjie"><h6>内容简介：</h6><p>山雨来的那夜，沈砚在旧祠里捡到一枚断碑。</p></div>
        </body></html>
        """;

    private const string ChaptersHtml = """
        <html><body>
        <ul class="mulu_list">
          <li><a href="/book/9001/aaa.html">第一章 山雨欲来</a></li>
          <li><a href="/book/9001/bbb.html">第二章 旧祠</a></li>
          <li><a href="/book/9001/ccc.html">第三章 断碑</a></li>
        </ul>
        </body></html>
        """;

    private const string ChapterHtml = """
        <html><body>
        <div class="read-content j_readContent">
          <p>灯芯爆了一下，屋里的影子跟着晃。</p>
          <p>　信是三日前到的，封口的火漆完好无损。</p>
          <p>本站域名 www.example.com 请收藏</p>
          <p></p>
          <p>来人笑了一下，跨过门槛。</p>
          <p>来人笑了一下，跨过门槛。</p>
        </div>
        </body></html>
        """;

    [Fact]
    public void ParseSearch_StripsRankingPrefixAndReadsFields()
    {
        var result = AliceswParser.ParseSearch(SearchHtml, 1);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("101", result.Items[0].Id);
        Assert.Equal("山海旧事", result.Items[0].Title);
        Assert.Equal("陈砚", result.Items[0].Author);
        Assert.Equal("已完结", result.Items[0].Status);
        Assert.Equal("42.80万字", result.Items[0].WordCountText);
        Assert.Equal(["玄幻", "群像"], result.Items[0].Tags);
        Assert.Equal("06-08 08:56", result.Items[0].UpdatedText);
        Assert.Equal(7, result.TotalPages);
    }

    [Fact]
    public void ParseDetail_ReadsMetaTagsAndIntro()
    {
        var detail = AliceswParser.ParseDetail(DetailHtml, "101");

        Assert.Equal("山海旧事", detail.Title);
        Assert.Equal("陈砚", detail.Author);
        Assert.Equal("玄幻", detail.Category);
        Assert.Equal("已完结", detail.Status);
        Assert.Equal("42.80万字", detail.WordCountText);
        Assert.Equal(3, detail.ChapterCount);
        Assert.Equal("https://img.example.com/c1.webp", detail.CoverUrl);
        Assert.Equal("山雨来的那夜，沈砚在旧祠里捡到一枚断碑。", detail.Intro);
        Assert.Equal(["玄幻", "群像"], detail.Tags);
        Assert.Equal("第三章 断碑", detail.LatestChapterTitle);
    }

    [Fact]
    public void ParseChapters_KeepsBookIdAndKey()
    {
        var chapters = AliceswParser.ParseChapters(ChaptersHtml);

        Assert.Equal(3, chapters.Count);
        Assert.Equal("9001/aaa", chapters[0].Id);
        Assert.Equal(0, chapters[0].Index);
        Assert.Equal("第三章 断碑", chapters[2].Title);
    }

    private const string ListHtml = """
        <html><body>
        <div class="rec_rullist">
          <ul>
            <li class="one"><span>1</span></li>
            <li class="sev"><span><a href="/lists/62.html">玄幻小说</a></span></li>
            <li class="two"><a href="/novel/54006.html">仙子们都太粘人了</a></li>
            <li class="three"><a href="/book/55343/aaa.html">第147章 沙海惊变</a></li>
            <li class="four">千早家爱音</li>
            <li class="five">58.13万</li>
            <li class="six">2026-11-05</li>
            <li class="diyhot">27</li>
          </ul>
          <ul>
            <li class="one"><span>2</span></li>
            <li class="sev"><span><a href="/lists/62.html">玄幻小说</a></span></li>
            <li class="two"><a href="/novel/53995.html">琼明神女录 典藏版</a></li>
            <li class="three"><a href="/book/55332/bbb.html">第十五章 番外</a></li>
            <li class="four">倒悬山剑气长存</li>
            <li class="five">122.96万</li>
            <li class="six">2026-11-03 12:00</li>
            <li class="diyhot">5845</li>
          </ul>
        </div>
        <a href="/lists/62.html?page=2">2</a><a href="/lists/62.html?page=9">9</a>
        </body></html>
        """;

    [Fact]
    public void ParseList_ReadsTableRowsAndPaging()
    {
        var result = AliceswParser.ParseList(ListHtml, 1);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("54006", result.Items[0].Id);
        Assert.Equal("仙子们都太粘人了", result.Items[0].Title);
        Assert.Equal("千早家爱音", result.Items[0].Author);
        Assert.Equal("玄幻", result.Items[0].Category);
        Assert.Equal("58.13万字", result.Items[0].WordCountText);
        Assert.Equal("27", result.Items[0].HitsText);
        Assert.Equal("第147章 沙海惊变", result.Items[0].LatestChapterTitle);
        Assert.Equal(9, result.TotalPages);
    }

    [Fact]
    public void ParseCategories_KeepsShortIdsAndDropsBookCategories()
    {
        const string html = """
            <html><body>
            <a href="/lists/62.html">玄幻</a>
            <a href="/lists/64.html">都市</a>
            <a href="/lists/65.html">乱伦小说</a>
            <a href="/lists/53995.html">玄幻</a>
            <a href="/lists/62.html">玄幻</a>
            </body></html>
            """;

        var categories = AliceswParser.ParseCategories(html);

        Assert.Equal(3, categories.Count);
        Assert.Equal(["62", "64", "65"], categories.Select(c => c.Id));
        Assert.Equal("乱伦", categories[2].Name);
    }

    [Fact]
    public void ParseChapterText_DropsNoiseAndDuplicates()
    {
        var text = AliceswParser.ParseChapterText(ChapterHtml);

        Assert.Contains("灯芯爆了一下", text);
        Assert.DoesNotContain("example.com", text);
        // 噪音段与重复段被剔除：同一句只留一次
        Assert.Equal(1, CountOccurrences(text, "来人笑了一下"));
        // 每段以两个全角空格开头，与本地 txt 排版一致
        Assert.StartsWith("　　", text);
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void TextIndexer_SplitsOnChapterPattern()
    {
        var text = "第1章 开始\n\n　　正文一。\n\n第2章 继续\n\n　　正文二。\n\n第3章 结束\n\n　　正文三。";
        var spans = NovelTextIndexer.Split(text, NovelTextIndexer.DefaultPattern);

        Assert.Equal(3, spans.Count);
        Assert.Equal("第1章 开始", spans[0].Title);
        Assert.Equal(0, spans[0].Offset);
        Assert.True(spans[1].Offset > spans[0].Offset);
        Assert.Contains("第2章 继续", text.Substring(spans[1].Offset, spans[1].Length));
    }

    [Fact]
    public void TextIndexer_ReturnsEmptyWhenOnlyOneChapter()
    {
        var spans = NovelTextIndexer.Split("第1章 唯一\n\n　　正文", NovelTextIndexer.DefaultPattern);
        Assert.Empty(spans);
    }

    [Fact]
    public async Task LocalMatcher_ExactAndPossible()
    {
        var root = Path.Combine(Path.GetTempPath(), "rg-novel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "玄幻"));
        await File.WriteAllTextAsync(Path.Combine(root, "玄幻", "山海旧事(校对版).txt"), "x");

        var indexPath = Path.Combine(root, "index.json");
        var json = $$"""
        {
          "root": "{{root.Replace("\\", "\\\\")}}",
          "total": 1,
          "structure": ["玄幻"],
          "files": [
            { "name": "山海旧事(校对版).txt", "primaryTag": "玄幻", "tags": ["玄幻"], "relativePath": "玄幻/山海旧事(校对版).txt", "size": 10 }
          ]
        }
        """;
        await File.WriteAllTextAsync(indexPath, json);

        var index = new NovelIndexService();
        Assert.NotNull(await index.LoadAsync(indexPath));

        var matcher = new NovelLocalMatcher(index);
        var exact = matcher.Find("山海旧事");
        Assert.NotNull(exact);
        Assert.Equal(LocalMatchKind.Exact, exact!.Kind);

        var possible = matcher.Find("山海旧事外传");
        Assert.NotNull(possible);
        Assert.Equal(LocalMatchKind.Possible, possible!.Kind);

        Assert.Null(matcher.Find("完全无关的书名"));

        Directory.Delete(root, true);
    }

    [Fact]
    public void TagResolver_MatchesCategoryNamesOnly()
    {
        var categories = new List<NovelCategory>
        {
            new() { Id = "62", Name = "玄幻" },
            new() { Id = "64", Name = "都市" },
        };

        // 同名 / 带「小说」后缀 / 带 # 前缀 都算命中
        Assert.Equal("62", NovelTagResolver.MatchCategory("玄幻", categories)!.Id);
        Assert.Equal("62", NovelTagResolver.MatchCategory("玄幻小说", categories)!.Id);
        Assert.Equal("64", NovelTagResolver.MatchCategory("#都市", categories)!.Id);

        // 自由标签不该被当成分类
        Assert.Null(NovelTagResolver.MatchCategory("群像", categories));
        Assert.Null(NovelTagResolver.MatchCategory("  ", categories));
        Assert.Null(NovelTagResolver.MatchCategory("玄幻", Array.Empty<NovelCategory>()));
    }

    [Theory]
    [InlineData("《琼明神女录》作者：倒悬山剑气长存 精校版", "琼明神女录")]
    [InlineData("仙子们都太粘人了[1-100完]", "仙子们都太粘人了")]
    [InlineData("001. 山海旧事（校对版）", "山海旧事")]
    [InlineData("长夜将明 完结", "长夜将明")]
    [InlineData("归墟纪 无删减 完整版", "归墟纪")]
    [InlineData("TXT 海边的卡夫卡", "海边的卡夫卡")]
    public void CoverTitle_NormalizesVersionNoise(string input, string expected)
        => Assert.Equal(expected, NovelCoverService.NormalizeTitle(input));

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }
        return count;
    }
}
