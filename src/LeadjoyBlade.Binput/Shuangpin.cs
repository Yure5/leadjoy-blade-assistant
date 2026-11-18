namespace LeadjoyBlade.Binput;

/// <summary>
/// 输入方案。方阵 / 轮盘是**外观**, 这两个是**打法** —— 互不干涉。
/// 九键拼音 = 一直以来的 T9(数字串任意长度, 引擎自己切成音节);
/// 双拼 = 两键一个音节, 9 颗键各绑一组声母 / 韵母, 由用户自己定。
/// </summary>
public enum PinyinMode { T9, Shuangpin }

public static class PinyinModes
{
    public static string Name(PinyinMode m) => m == PinyinMode.Shuangpin ? "双拼" : "九键拼音";
    public static PinyinMode Next(PinyinMode m) => m == PinyinMode.T9 ? PinyinMode.Shuangpin : PinyinMode.T9;
}

/// <summary>
/// 双拼的数据与解析。
/// <para>【这里不出汉字】和 T9Engine 一个规矩: 只把「两颗键」解成「可能的拼音音节」,
/// 用户挑一个之后照旧由 <see cref="InputCore.SendPinyin"/> 逐字母发给系统输入法,
/// 汉字和联想全归系统输入法。所以拼音行 / 候选网格 / RB 上屏 / Y 切分整套都能复用。</para>
/// <para>【音节表不自己造】合法音节就是 <see cref="T9Engine"/> 里那 409 个。
/// 解析时是**拿音节表反过来验**「(声母, 韵母) 能不能生成这个音节」, 而不是自己拼字符串去猜
/// —— 因为 ü 在 j/q/x 后写 u、在 l/n 后写 v, 零声母 i/u/ü 又要改写成 yi/wu/yu,
/// 靠拼字符串必漏(见 ZeroForm / FinalMatches)。</para>
/// </summary>
public static class Shuangpin
{
    /// <summary>
    /// 零声母的**内部标签**: 存档、59 项清单、互斥判断全用它, 不直接摆给用户看。
    /// 2.4.17 起面向用户的每一处都显示 <see cref="ZERO_MARK"/>(三角), 但这里
    /// 故意不改成符号 —— 老存档里存的还是这三个字, 改了就得做数据迁移。
    /// </summary>
    public const string ZERO = "零声母";

    /// <summary>
    /// 零声母的显示符号: 空心三角 △。
    /// <para>【查过一圈别的方案】九键双拼没有统一记号: 手机 1agb 九键方案直接写
    /// 「零声母」三个字, 专利 CN1851624A 的九格双拼用一个撇号 <c>'</c> 占位;
    /// 26 键那几套(微软 / 搜狗 / 智能ABC / 紫光)干脆把 <c>O</c> 键当零声母,
    /// 小鹤则是不设零声母、把韵母键连打两下(aa / oo / ee)。所以没有"标准符号"
    /// 可抄, 按用户拍板用 △: 单个字符, 和 b / an 这些字母走同一档字号,
    /// 既不占三个汉字那么宽, 又是个一眼能认出的"空 / 零"记号。</para>
    /// </summary>
    public const string ZERO_MARK = "△";

    /// <summary>23 个声母 + 零声母 = 24 项。单字母声母照样可自定义(用户拍板)。</summary>
    public static readonly string[] Initials =
    {
        "b", "p", "m", "f", "d", "t", "n", "l", "g", "k", "h", "j", "q", "x",
        "zh", "ch", "sh", "r", "z", "c", "s", "y", "w", ZERO,
    };

    /// <summary>
    /// 35 个韵母。小学课本那 24 个不含带介音的整段(ia / uang / iong …),
    /// 双拼必须把它们也当一项, 否则 jia / zhuang / xiong 全打不出来。
    /// 清单抄自自然码完整键位表 / 专利 CN105759985A 的「35 个韵母元素」, 不是推导出来的。
    /// </summary>
    public static readonly string[] Finals =
    {
        "a", "o", "e", "i", "u", "ü",
        "ai", "ei", "ui", "ao", "ou", "iu", "ie", "üe", "er",
        "an", "en", "in", "un", "ün", "ang", "eng", "ing", "ong",
        "ia", "ua", "uo", "uai", "uan", "uang", "ian", "iao", "iang", "iong", "üan",
    };

    /// <summary>
    /// 自定义页里铺的全部 59 项, **按字母表顺序**: 零声母(特殊项)在最前,
    /// 接着 23 声母按字典序(b c ch d f …), 再 35 韵母按「首字母 -> 长度 -> 字典序」
    /// (an 在 ang 前, 短的在前)。排序只在建表时算一次。
    /// </summary>
    public static readonly string[] All;

    static Shuangpin()
    {
        var ini = Initials.Where(x => x != ZERO).OrderBy(x => x, StringComparer.Ordinal);
        var fin = Finals.OrderBy(x => x[0])
                        .ThenBy(x => x.Length)
                        .ThenBy(x => x, StringComparer.Ordinal);
        All = new[] { ZERO }.Concat(ini).Concat(fin).ToArray();

        ZeroForm = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "a", ["o"] = "o", ["e"] = "e", ["i"] = "yi", ["u"] = "wu", ["ü"] = "yu",
            ["ai"] = "ai", ["ei"] = "ei", ["ui"] = "wei", ["ao"] = "ao", ["ou"] = "ou",
            ["iu"] = "you", ["ie"] = "ye", ["üe"] = "yue", ["er"] = "er",
            ["an"] = "an", ["en"] = "en", ["in"] = "yin", ["un"] = "wen", ["ün"] = "yun",
            ["ang"] = "ang", ["eng"] = "eng", ["ing"] = "ying", ["ong"] = "weng",
            ["ia"] = "ya", ["ua"] = "wa", ["uo"] = "wo", ["uai"] = "wai",
            ["uan"] = "wan", ["uang"] = "wang", ["ian"] = "yan", ["iao"] = "yao",
            ["iang"] = "yang", ["iong"] = "yong", ["üan"] = "yuan",
        };
    }

    static readonly Dictionary<string, string> ZeroForm;

    public static bool IsInitial(string s) => Array.IndexOf(Initials, s) >= 0;
    public static string KindOf(string s) => s == ZERO ? "零声母" : IsInitial(s) ? "声母" : "韵母";

    // ================= 9 组绑定(下标 1..9) =================
    //
    // 存成一行文本: 每颗键一组标签, 组间用 '|' 分隔, 组内用 ',' 分隔。
    // 标签里只有字母 / ü / ○, 不会撞这两个分隔符。空串 = 一颗键都没配(默认全空)。

    public static string[] Empty()
    {
        var b = new string[10];
        for (int i = 0; i < b.Length; i++) b[i] = "";
        return b;
    }

    public static string[] Decode(string s)
    {
        var b = Empty();
        if (string.IsNullOrEmpty(s)) return b;
        var parts = s.Split('|');
        for (int i = 0; i < parts.Length && i < 9; i++) b[i + 1] = parts[i];
        return b;
    }

    public static string Encode(string[] b)
    {
        if (b == null) return "";
        var a = new string[9];
        for (int i = 1; i <= 9; i++) a[i - 1] = b.Length > i ? b[i] ?? "" : "";
        return string.Join("|", a);
    }

    public static List<string> ItemsOf(string[] b, int key)
    {
        var res = new List<string>();
        if (b == null || key < 1 || key > 9 || b.Length <= key) return res;
        foreach (var raw in (b[key] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var x = raw == "○" ? ZERO : raw;             // 兼容测试版存过的旧标签
            if (Array.IndexOf(All, x) >= 0) res.Add(x);  // 存档里的野标签一律不认
        }
        return res;
    }

    public static int CountOf(string[] b, int key) => ItemsOf(b, key).Count;

    public static bool Has(string[] b, int key, string item) => ItemsOf(b, key).Contains(item);

    /// <summary>这一项被哪颗键绑了(1..9); 0 = 没人绑。</summary>
    public static int OwnerOf(string[] b, string item)
    {
        for (int k = 1; k <= 9; k++) if (Has(b, k, item)) return k;
        return 0;
    }

    /// <summary>点一下: 本键有就取消, 没有就绑上。同一项绝不允许出现在两颗键上。</summary>
    public static void Toggle(string[] b, int key, string item)
    {
        if (b == null || key < 1 || key > 9 || b.Length <= key) return;
        int owner = OwnerOf(b, item);
        if (owner == key)                                   // 再点一下 = 取消选择
        {
            var l = ItemsOf(b, key);
            l.Remove(item);
            b[key] = string.Join(",", l);
            return;
        }
        if (owner != 0) return;                            // 别的键已经占了 -> 置灰, 不许抢
        var add = ItemsOf(b, key);
        add.Add(item);
        b[key] = string.Join(",", add);
    }

    // ================= 两键 -> 候选音节 =================

    /// <summary>ü 系列在音节表里的写法: j/q/x 后写 u, l/n 后写 v, 零声母看 ZeroForm。</summary>
    static bool FinalMatches(string rest, string fin)
    {
        if (rest == fin) return true;
        return fin switch
        {
            "ü" => rest is "u" or "v",
            "üe" => rest is "ue" or "ve",
            "ün" => rest is "un" or "vn",
            "üan" => rest is "uan" or "van",
            _ => false,
        };
    }

    static bool ZeroMatches(string syl, string fin) =>
        ZeroForm.TryGetValue(fin, out var z) && z == syl;

    /// <summary>
    /// 第 1 键(声母组) + 第 2 键(韵母组) 能拼出哪些合法音节。
    /// 全部列出来交给拼音行 / 候选网格 —— 用户说「一键多义直接把可能性列进候选就行」。
    /// </summary>
    public static List<string> PairSyllables(char k1, char k2, string[] b)
    {
        var res = new List<string>();
        if (k1 < '1' || k1 > '9' || k2 < '1' || k2 > '9') return res;
        var inis = ItemsOf(b, k1 - '0').Where(IsInitial).ToList();
        var fins = ItemsOf(b, k2 - '0').Where(x => !IsInitial(x)).ToList();
        if (inis.Count == 0 || fins.Count == 0) return res;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ini in inis)
            foreach (var fin in fins)
                foreach (var syl in T9Engine.AllSyllables)
                {
                    bool ok = ini == ZERO
                        ? ZeroMatches(syl, fin)
                        : syl.StartsWith(ini, StringComparison.Ordinal)
                          && FinalMatches(syl.Substring(ini.Length), fin);
                    if (ok && seen.Add(syl)) res.Add(syl);
                }
        // 常用的在前(和九键拼音一个手感); 单音节"我常发"也要顶上来(比如总打「啊」的 a)。
        // 以前只留 12 条, 冷一点的首音节(lao)直接被砍掉, 候选里再也找不着 ——
        // 放宽到一屏(28), 剩下那些名额交给 SegsOf 轮转分配。
        res.Sort((a, z) => (T9Engine.HotOf(z) + T9Engine.LearnBonus(z))
                          .CompareTo(T9Engine.HotOf(a) + T9Engine.LearnBonus(a)));
        if (res.Count > 28) res.RemoveRange(28, res.Count - 28);
        return res;
    }

    /// <summary>键面 / 自定义页主标题 / 拼音行里零声母一律写成 △, 和字母同一档字号。</summary>
    public static string ShortOf(string x) => x == ZERO ? ZERO_MARK : x;

    /// <summary>粗略的可视宽度: 半角字母算 1, 汉字 / ü / △ 算 2。</summary>
    static int VisualWidth(string x)
    {
        int w = 0;
        foreach (char c in x) w += c < 128 ? 1 : 2;
        return w;
    }

    /// <summary>
    /// 双拼九键首页某颗键显示的若干行文字。
    /// <para>【2.4.19 声母 / 韵母分家】声母(△ 零声母算声母)一组在前, 韵母另起一行在后,
    /// 两组之间换行但不留空行; 组内短项在前, **只按宽度换行**(一行写满 cap 再换)。
    /// 文字块整体居中, 内容多时以中心为基准往上下(行数)、左右(行宽)扩展;
    /// 行数上限与每行宽度由 <paramref name="rows"/> / <paramref name="cap"/> 给(见
    /// <see cref="KeyFaceSpec"/>)。</para>
    /// <para>只有行数真的用尽才丢后面的项; 零声母 △ 排最前(和自定义页一个次序)。</para>
    /// </summary>
    public static string[] KeyLines(string[] b, int key, int rows, int cap)
    {
        rows = Math.Max(1, rows);
        cap = Math.Max(1, cap);
        var res = new string[rows];
        Array.Fill(res, "");
        var raw = ItemsOf(b, key);
        // 【2.4.19 声母 / 韵母分家】声母(含 △ 零声母)整组排前面, 韵母整组另起一行;
        // 两组之间只换行, **不插空行**。组内短项在前、按宽度贪心换行。
        string[][] groups =
        {
            raw.Where(IsInitial).ToArray(),
            raw.Where(x => !IsInitial(x)).ToArray(),
        };
        var lines = new List<string>();
        var cur = new List<string>();
        int w = 0;
        foreach (var g in groups)
        {
            if (g.Length == 0) continue;              // 空组不制造空行
            if (cur.Count > 0)                        // 上一组收尾 -> 强制换行
            {
                lines.Add(string.Join(" ", cur));
                cur.Clear(); w = 0;
                if (lines.Count >= rows) break;
            }
            foreach (var x in g.Select(ShortOf)
                               .OrderBy(x => x == ZERO_MARK ? -1 : x.Length)
                               .ThenBy(x => x, StringComparer.Ordinal))
            {
                int vw = VisualWidth(x);
                int add = vw + (cur.Count > 0 ? 1 : 0);
                if (cur.Count > 0 && w + add > cap)
                {
                    lines.Add(string.Join(" ", cur));
                    if (lines.Count >= rows) break;   // 行数用尽: 后面的不写
                    cur.Clear(); w = 0; add = vw;
                }
                if (w + add > cap) continue;          // 这一项自己就超宽: 不写它
                cur.Add(x); w += add;
            }
            if (lines.Count >= rows) break;
        }
        if (cur.Count > 0 && lines.Count < rows) lines.Add(string.Join(" ", cur));
        for (int i = 0; i < lines.Count && i < rows; i++) res[i] = lines[i];
        return res;
    }

    /// <summary>三行 / 14 宽的旧默认, 给纯逻辑探针量排版用(界面走上面那个带参的)。</summary>
    public static string[] KeyLines(string[] b, int key) => KeyLines(b, key, 3, 14);

    /// <summary>
    /// 某颗键的键面能给几行、每行多宽(单位 = 一个半角字母)。
    /// <para>【为什么轮盘要按方位分开】扇环不是方块: 上下两瓣(2/8)是"竖着长、
    /// 横着窄", 左右两瓣(4/6)反过来"横着宽、竖着矮", 四个斜瓣两头都收,
    /// 中心圆的内接方块最小。统一给一套数, 要么横着溢出瓣外, 要么白白空着一半。
    /// 这里按每瓣的内接矩形给行数和行宽, 字号仍是原来那档(12 / 11), 一个都不改。</para>
    /// <para>数是从 SlotPanel 的几何反推的: 圆盘 Φ360, 中心圆 Φ96, 扇环内沿 54、
    /// 外沿约 139, 文字盒 150×104 再整体缩 0.82; 方阵那颗键 112 见方。</para>
    /// </summary>
    public static (int rows, int cap) KeyFaceSpec(bool wheel, int slot)
    {
        if (!wheel) return (6, 17);                     // 方阵: 六行铺满, 左右各留约 7px
        return slot switch
        {
            2 or 8 => (6, 14),                         // 上 / 下瓣: 竖向长 -> 行数多、每行窄
            4 or 6 => (5, 17),                         // 左 / 右瓣: 横向宽 -> 每行宽、行数少
            5 => (4, 12),                              // 中心圆: 圆里只塞得下内接方块
            _ => (5, 14),                              // 四个斜瓣: 两头都收
        };
    }

    /// <summary>
    /// 数字串 -> 拼音切分。双拼下**每 2 位一个音节**; 落单的那一位给「拼了一半」的
    /// 反馈(列出这颗键绑的声母), 免得按下第一颗键拼音行一片空白 —— 「按下键必须有反馈」
    /// 是不变量。全空(还没自定义)时一条都给不出, 就是打不出字, 不崩也不出怪串。
    /// </summary>
    public static List<string[]> SegsOf(string digits, string[] b)
    {
        var res = new List<string[]>();
        if (string.IsNullOrEmpty(digits) || b == null) return res;
        int pairs = digits.Length / 2;
        bool odd = digits.Length % 2 == 1;

        List<string> tail = null;
        if (odd)
        {
            // 【零声母不进拼音行】它只是"这一键没有声母"的标记, 不是拼音。
            // 以前直接把标签塞进候选, 按一下就蹦出「零声母」三个汉字 —— 拼音行
            // 里混进一个中文词, 看着像打错字(用户报过)。现在优先列这颗键绑的
            // 真声母, 再列韵母; 只有"这颗键光绑了零声母"才退回去给 △ 一个符号,
            // 保住「按下键必须有反馈」这条不变量。
            int k = digits[^1] - '0';
            var raw = ItemsOf(b, k);
            tail = raw.Where(x => x != ZERO && IsInitial(x)).Select(ShortOf).ToList();
            if (tail.Count == 0)
                tail = raw.Where(x => x != ZERO).Select(ShortOf).ToList();
            if (tail.Count == 0 && raw.Contains(ZERO)) tail.Add(ZERO_MARK);
            if (tail.Count > 12) tail.RemoveRange(12, tail.Count - 12);
        }
        if (pairs == 0)
        {
            if (tail != null) foreach (var t in tail) res.Add(new[] { t });
            return res;
        }

        const int MaxSegs = 28;                  // 和九键 SegMax 一致
        var cur = new List<string[]> { Array.Empty<string>() };
        for (int p = 0; p < pairs; p++)
        {
            var g = PairSyllables(digits[p * 2], digits[p * 2 + 1], b);
            if (g.Count == 0) return res;       // 有一组拼不出 -> 整串不成立
            // 【2.4.20 轮转, 别让名额被头两个首音节吃光】以前是"外层前缀 × 内层尾音",
            // 28 条名额全给最前面两三个首音节配齐了所有尾音, 后面的首音节(lao)
            // 一条都进不来 —— 用户打 lao ren 时发现拼音行只有 lai 打头的。
            // 现在一轮一轮来: 第一轮给**每个**前缀各配一个尾音(首音节全覆盖),
            // 第二轮再补变化。于是前几行就是"第一个拼音的所有可能性"。
            var next = new List<string[]>();
            for (int round = 0; round < g.Count && next.Count < MaxSegs; round++)
                foreach (var pre in cur)
                {
                    if (next.Count >= MaxSegs) break;
                    var a = new string[pre.Length + 1];
                    Array.Copy(pre, a, pre.Length);
                    a[^1] = g[round];
                    next.Add(a);
                }
            cur = next;
            if (cur.Count == 0) return res;
        }

        if (odd && tail != null && tail.Count > 0)
        {
            var withTail = new List<string[]>();
            for (int round = 0; round < tail.Count && withTail.Count < MaxSegs; round++)
                foreach (var pre in cur)
                {
                    if (withTail.Count >= MaxSegs) break;
                    var a = new string[pre.Length + 1];
                    Array.Copy(pre, a, pre.Length);
                    a[^1] = tail[round];
                    withTail.Add(a);
                }
            cur = withTail;
        }

        // 【接上常用拼音记忆】九键走的是 T9Engine.RankOrder(词频 + 音节热度 + 你发过
        // 几次), 双拼以前只按音节热度生成、不排这一刀, 于是打几次也不靠前;
        // 单发一个「啊」也一样。现在照同一套打分, 但先让**每个首音节各来一条最好的**
        // 排在前面, 保证"第一个拼音的可能性都露脸", 其余再按分数补。
        int blocks = pairs + (odd ? 1 : 0);
        var scored = cur.Select(s => new { S = s, Score = T9Engine.RankOrder(s, blocks) }).ToList();
        var best = scored.GroupBy(x => x.S.Length > 0 ? x.S[0] : "")
                         .Select(gp => gp.OrderByDescending(x => x.Score).First())
                         .OrderByDescending(x => x.Score).ToList();
        var picked = new HashSet<string[]>(best.Select(x => x.S));
        var rest = scored.Where(x => !picked.Contains(x.S))
                         .OrderByDescending(x => x.Score).ToList();
        foreach (var x in best.Concat(rest).Take(MaxSegs)) res.Add(x.S);
        return res;
    }
}
