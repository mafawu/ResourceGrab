using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResourceGrab.Core.Sources.Hitomi;

/// <summary>B-tree 索引节点（对应原版 search.rs 的 Node）。</summary>
public sealed class HitomiIndexNode
{
    public List<byte[]> Keys { get; init; } = new();
    public List<(long Offset, int Length)> Datas { get; init; } = new();
    public List<long> SubNodeAddresses { get; init; } = new();
}

/// <summary>hitomi 二进制索引解码：nozomi 列表、galleriesindex 版本、B-tree 节点与数据段。</summary>
public static class HitomiBinaryIndex
{
    /// <summary>解析 nozomi 文件：BigEndian Int32 画廊 id 序列，读到 EOF。</summary>
    public static List<int> ParseNozomiIds(byte[] data)
    {
        var ids = new List<int>(data.Length / 4);
        for (var offset = 0; offset + 4 <= data.Length; offset += 4)
        {
            ids.Add(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4)));
        }
        return ids;
    }

    /// <summary>解析 B-tree 节点（MAX_NODE_SIZE 字节）。</summary>
    public static HitomiIndexNode DecodeNode(byte[] data)
    {
        var span = data.AsSpan();
        var offset = 0;
        var node = new HitomiIndexNode();

        var keyCount = ReadInt32(span, ref offset);
        for (var i = 0; i < keyCount; i++)
        {
            var keySize = ReadInt32(span, ref offset);
            if (keySize <= 0 || keySize > 32)
            {
                throw new JmException($"fatal: !keySize || keySize > 32 (keySize={keySize})");
            }
            node.Keys.Add(span.Slice(offset, keySize).ToArray());
            offset += keySize;
        }

        var dataCount = ReadInt32(span, ref offset);
        for (var i = 0; i < dataCount; i++)
        {
            var dataOffset = ReadInt64(span, ref offset);
            var length = ReadInt32(span, ref offset);
            node.Datas.Add((dataOffset, length));
        }

        for (var i = 0; i <= HitomiConstants.B; i++)
        {
            node.SubNodeAddresses.Add(ReadInt64(span, ref offset));
        }

        return node;
    }

    /// <summary>从数据段字节解析画廊 id 列表：BigEndian Int32 数量 + id 序列。</summary>
    public static List<int> ParseGalleryIdsFromData(byte[] data)
    {
        var span = data.AsSpan();
        var offset = 0;
        var count = ReadInt32(span, ref offset);
        if (count <= 0 || count > 10_000_000)
        {
            throw new JmException($"number_of_galleryids `{count}` 超出合理范围");
        }

        var ids = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            ids.Add(ReadInt32(span, ref offset));
        }
        return ids;
    }

    /// <summary>sha256 前 4 字节（搜索关键词哈希）。</summary>
    public static byte[] HashTerm(string term)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(term));
        return hash.AsSpan(0, 4).ToArray();
    }

    /// <summary>逐字节比较两个 key（与原版 compare_arrays 一致）。</summary>
    public static int CompareArrays(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var top = Math.Min(a.Length, b.Length);
        for (var i = 0; i < top; i++)
        {
            if (a[i] < b[i])
            {
                return -1;
            }
            if (a[i] > b[i])
            {
                return 1;
            }
        }
        return 0;
    }

    /// <summary>定位 key：返回 (是否存在, 索引)。</summary>
    public static (bool Found, int Index) LocateKey(ReadOnlySpan<byte> key, HitomiIndexNode node)
    {
        for (var i = 0; i < node.Keys.Count; i++)
        {
            var cmp = CompareArrays(key, node.Keys[i]);
            if (cmp <= 0)
            {
                return (cmp == 0, i);
            }
        }
        return (false, node.Keys.Count);
    }

    /// <summary>是否叶子节点（全部子节点地址为 0）。</summary>
    public static bool IsLeaf(HitomiIndexNode node)
        => node.SubNodeAddresses.All(addr => addr == 0);

    private static int ReadInt32(ReadOnlySpan<byte> span, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(span.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> span, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt64BigEndian(span.Slice(offset, 8));
        offset += 8;
        return value;
    }
}
