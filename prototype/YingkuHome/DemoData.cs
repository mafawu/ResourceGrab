using System.Windows.Media;
using System.Windows.Media.Imaging;
using YingkuHome.Models;

namespace YingkuHome;

/// <summary>Demo library: 12 generated titles, no real media on disk.</summary>
public static class DemoData
{
    private static ImageSource Load(string file, int decodeWidth)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri($"pack://application:,,,/Assets/{file}", UriKind.Absolute);
        bmp.DecodePixelWidth = decodeWidth;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static (string code, string title, string en, string hero, string year, string dur, string actors, string desc)[] Rows =>
    [
        ("DEMO-001", "逐光之旅", "CHASING THE FIRST LIGHT",
            "逐光之旅：穿越群山与海岸\n寻找世界尽头的第一束晨光",
            "2025", "2 小时 40 分", "林然、周远",
            "两位旅行者带着一张旧地图踏上旅程，在群山、海岸与陌生城市之间，寻找记忆中的灯塔，也重新发现生活的方向。"),
        ("DEMO-002", "星海远航", "BEYOND THE TIDE",
            "星海远航：向月光借一条航线\n在潮汐之间辨认归途",
            "2024", "1 小时 58 分", "沈屿、苏晚",
            "一艘没有目的地的船，载着三个不肯上岸的人，在夜海里打捞彼此的秘密。"),
        ("DEMO-003", "灯塔来信", "LETTERS FROM THE LIGHT",
            "灯塔来信：写给守夜人的长信\n每一页都亮着一盏灯",
            "2025", "1 小时 46 分", "程野、白露",
            "守塔人每天写一封信，投进从不寄出的邮筒，直到某个雾夜有人敲响了门。"),
        ("DEMO-004", "森林之约", "WHERE THE FOREST WAITS",
            "森林之约：在苔藓与松脂之间\n赴一场迟到十年的约定",
            "2023", "2 小时 12 分", "顾清、叶知",
            "童年许下的约定被森林保存了十年，回去的人却已经认不出彼此。"),
        ("DEMO-005", "山间列车", "THE MOUNTAIN LINE",
            "山间列车：沿着高架桥向北\n把整个夏天留在车窗里",
            "2026", "1 小时 39 分", "许南、温以",
            "一列只有四节车厢的慢车，载着整个夏天穿过隧道与高架桥。"),
        ("DEMO-006", "雨后之城", "AFTER THE RAIN",
            "雨后之城：霓虹落在湿透的街\n我们踩着倒影走回家",
            "2024", "1 小时 51 分", "江聿、黎雾",
            "雨停之后的城市像一面镜子，把每个人没说出口的话照了出来。"),
        ("DEMO-007", "雪山回响", "ECHOES OF SNOW",
            "雪山回响：翻过最后一道垭口\n听见群峰替我回答",
            "2025", "2 小时 05 分", "陆迟、安禾",
            "登山队在雪线之上失去联络，留下的只有一段反复回响的录音。"),
        ("DEMO-008", "夏日乐章", "A SUMMER MELODY",
            "夏日乐章：风吹过草甸与手风琴\n整个下午都是慢板",
            "2023", "1 小时 44 分", "周叙、南乔",
            "小镇的手风琴手用一个夏天，把散落的邻居重新拧成一支乐队。"),
        ("DEMO-009", "沙海行记", "ACROSS THE DUNES",
            "沙海行记：跟着商队的铃声\n把影子留在沙丘背面",
            "2024", "2 小时 18 分", "沙舟、阿禾",
            "商队沿着古河道穿越沙海，驼铃替他们记住了所有走过的路。"),
        ("DEMO-010", "蔚蓝世界", "INTO THE BLUE",
            "蔚蓝世界：下潜到光消失的深度\n那里安静得像另一个星球",
            "2026", "1 小时 37 分", "海宁、深青",
            "潜水员在无光层发现一片会呼吸的珊瑚，也发现了自己的恐惧。"),
        ("DEMO-011", "星河彼岸", "THE OTHER SHORE",
            "星河彼岸：把名字写进星图\n等一次跨越光年的回信",
            "2025", "2 小时 26 分", "沈玦、林见",
            "深空观测站只剩一个人值班，他每天向同一个坐标发送同一段问候。"),
        ("DEMO-012", "极光之境", "UNDER THE AURORA",
            "极光之境：在北纬六十九度\n等天空把绿色泼下来",
            "2024", "1 小时 55 分", "北野、祈夏",
            "极夜里的气象站，两个人靠一盏灯和一台老收音机熬过漫长冬天。"),
    ];

    public static HomeViewModel Build()
    {
        var all = new List<DemoItem>();
        for (var i = 0; i < Rows.Length; i++)
        {
            var r = Rows[i];
            var n = i + 1;
            all.Add(new DemoItem
            {
                Code = r.code,
                Title = r.title,
                Subtitle = r.en,
                HeroTitle = r.hero,
                Year = r.year,
                Duration = r.dur,
                Actors = r.actors,
                Description = r.desc,
                Poster = Load($"poster_{n:00}.png", 480),
                Still = Load($"still_{n:00}.png", 1000),
                Backdrop = Load($"backdrop_{n:00}.png", 1600),
            });
        }

        return new HomeViewModel
        {
            RecentItems = all.Take(8).ToList(),
            AddedItems = all.Skip(4).ToList(),
        };
    }
}
