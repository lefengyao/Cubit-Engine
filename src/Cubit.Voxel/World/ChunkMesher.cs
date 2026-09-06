using System.Buffers;
using Cubit.Voxel.Textures;

namespace Cubit.Voxel.World;

/// <summary>
/// 区块网格化：只生成暴露面（相邻为空气/透明则补面），输出 位置+UV+明暗。
/// 明暗 = 面基准色 × 光照系数 × AO（环境光遮蔽，角落被邻块遮挡时压暗）。
/// </summary>
public static class ChunkMesher
{
    private const int VertexStride = ChunkMeshData.VertexFloatStride;
    private const float UnlitAmbient = 0.10f;
    // 对齐原版 cross.json 的 from 0.8..15.2 经 Y 轴 45 度旋转后的投影范围。
    private const float CrossMin = 0.18180195f;
    private const float CrossMax = 0.81819805f;
    private const float FluidSurfaceInset = 0.001f;

    private static readonly (int Dx, int Dy, int Dz, float Shade)[] Faces =
    [
        (0, 1, 0, 1.00f), // 顶
        (0, -1, 0, 0.60f), // 底
        (0, 0, 1, 0.80f), // +Z
        (0, 0, -1, 0.80f), // -Z
        (1, 0, 0, 0.80f), // +X
        (-1, 0, 0, 0.80f), // -X
    ];

    // 每个面的 4 个角：局部坐标 + 瓦片内 UV (0..1)
    private static readonly (int X, int Y, int Z, float U, float V)[][] FaceCorners =
    [
        // 顶
        [(0, 1, 0, 0, 0), (1, 1, 0, 1, 0), (1, 1, 1, 1, 1), (0, 1, 1, 0, 1)],
        // 底
        [(0, 0, 0, 0, 0), (1, 0, 0, 1, 0), (1, 0, 1, 1, 1), (0, 0, 1, 0, 1)],
        // +Z
        [(0, 0, 1, 0, 1), (1, 0, 1, 1, 1), (1, 1, 1, 1, 0), (0, 1, 1, 0, 0)],
        // -Z
        [(0, 0, 0, 0, 1), (1, 0, 0, 1, 1), (1, 1, 0, 1, 0), (0, 1, 0, 0, 0)],
        // +X
        [(1, 0, 0, 0, 1), (1, 0, 1, 1, 1), (1, 1, 1, 1, 0), (1, 1, 0, 0, 0)],
        // -X
        [(0, 0, 0, 0, 1), (0, 0, 1, 1, 1), (0, 1, 1, 1, 0), (0, 1, 0, 0, 0)],
    ];

    // 每个面每个角的 3 个 AO 采样偏移（相对面方块）：side1 / side2 / 对角
    private static readonly (int X, int Y, int Z)[][][] CornerAoOffsets = BuildCornerAoOffsets();

    /// <summary>
    /// 可合并面必须保持同一方块状态、面外光照和四个 AO 角落。
    /// 这样合并四边形不会把原本的逐顶点光照插值改成新的渐变。
    /// </summary>
    private readonly record struct CubeFaceSignature(
        bool IsVisible,
        BlockState Block,
        byte Light,
        byte Ao0,
        byte Ao1,
        byte Ao2,
        byte Ao3)
    {
        public byte GetAo(int corner) => corner switch
        {
            0 => Ao0,
            1 => Ao1,
            2 => Ao2,
            3 => Ao3,
            _ => throw new ArgumentOutOfRangeException(nameof(corner)),
        };
    }

    /// <summary>贪心扫描的中间描述；用池化数组跨过精确输出数组分配边界。</summary>
    private readonly record struct GreedyCubeQuad(
        CubeFaceSignature Signature,
        int Face,
        int X,
        int Y,
        int Z,
        int Width,
        int Height);

    private static (int X, int Y, int Z)[][][] BuildCornerAoOffsets()
    {
        var result = new (int, int, int)[Faces.Length][][];
        for (var f = 0; f < Faces.Length; f++)
        {
            var (dx, dy, dz, _) = Faces[f];
            result[f] = new (int, int, int)[4][];
            for (var k = 0; k < 4; k++)
            {
                var corner = FaceCorners[f][k];
                // 两个切向轴（法线分量为 0 的轴）
                var axes = new List<int>(2);
                if (dx == 0) axes.Add(0);
                if (dy == 0) axes.Add(1);
                if (dz == 0) axes.Add(2);

                var cornerCoordinates = new[] { corner.X, corner.Y, corner.Z };
                var out1 = new int[3];
                var out2 = new int[3];
                out1[axes[0]] = cornerCoordinates[axes[0]] == 0 ? -1 : 1;
                out2[axes[1]] = cornerCoordinates[axes[1]] == 0 ? -1 : 1;

                // AO 从面外侧开始采样，再沿两个切向轴偏移。不能叠加顶点的 0/1
                // 坐标，否则正向角会读到 +2 格；也不能遗漏法线偏移，否则顶面会把
                // 同层地表误判为遮挡，导致天然地面异常偏暗而新放方块异常偏亮。
                var normal = new[] { dx, dy, dz };
                var side1 = new[] { normal[0] + out1[0], normal[1] + out1[1], normal[2] + out1[2] };
                var side2 = new[] { normal[0] + out2[0], normal[1] + out2[1], normal[2] + out2[2] };
                var diag = new[]
                {
                    normal[0] + out1[0] + out2[0],
                    normal[1] + out1[1] + out2[1],
                    normal[2] + out1[2] + out2[2],
                };
                result[f][k] =
                [
                    (side1[0], side1[1], side1[2]),
                    (side2[0], side2[1], side2[2]),
                    (diag[0], diag[1], diag[2]),
                ];
            }
        }

        return result;
    }

    private static bool IsOpaque(ChunkStore store, int wx, int wy, int wz) =>
        Chunk.IsValidY(wy) && BlockRegistry.IsOpaque(store.GetBlock(wx, wy, wz).Id);

    private static bool IsOpaque(ChunkMeshSnapshot snapshot, int x, int y, int z) =>
        BlockRegistry.IsOpaque(snapshot.GetBlock(x, y, z).Id);

    public static ChunkMeshLayers Mesh(ChunkStore store, int chunkX, int chunkZ)
    {
        using var snapshot = ChunkMeshSnapshot.Capture(store, chunkX, chunkZ);
        return Mesh(snapshot, CancellationToken.None);
    }

    private static bool IsVisibleFace(ChunkMeshSnapshot snapshot, int x, int y, int z, int face)
    {
        var (dx, dy, dz, _) = Faces[face];
        return !((dx != 0 || dz != 0) && !snapshot.HasHorizontalNeighbourData(x + dx, z + dz)) &&
            !BlockRegistry.IsOpaque(snapshot.GetBlock(x + dx, y + dy, z + dz).Id);
    }

    /// <summary>流体不生成底面；同一流体内部面和未知水平邻区的边界面均不生成。</summary>
    private static bool IsVisibleFluidFace(
        ChunkMeshSnapshot snapshot,
        BlockState fluid,
        int x,
        int y,
        int z,
        int face)
    {
        if (face == 1)
        {
            return false;
        }

        var (dx, dy, dz, _) = Faces[face];
        if ((dx != 0 || dz != 0) && !snapshot.HasHorizontalNeighbourData(x + dx, z + dz))
        {
            return false;
        }

        var neighbour = snapshot.GetBlock(x + dx, y + dy, z + dz);
        if (BlockRegistry.IsOpaque(neighbour.Id))
        {
            return false;
        }

        if (neighbour.Id != fluid.Id)
        {
            return true;
        }

        return !VoxelFluidState.TryDecode(neighbour, out var neighbourFluid) ||
            !VoxelFluidState.TryDecode(fluid, out var currentFluid) ||
            FluidSurfaceHeight(neighbourFluid) < FluidSurfaceHeight(currentFluid);
    }

    private static float FluidSurfaceHeight(VoxelFluidState fluid) =>
        fluid.Falling ? 1f : fluid.Level / (float)VoxelFluidState.SourceLevel;

    private static CubeFaceSignature CreateCubeFaceSignature(
        ChunkMeshSnapshot snapshot,
        int x,
        int y,
        int z,
        int face)
    {
        var block = snapshot.GetBlock(x, y, z);
        if (block.IsAir ||
            BlockRegistry.Get(block).RenderShape != BlockRenderShape.Cube ||
            !IsVisibleFace(snapshot, x, y, z, face))
        {
            return default;
        }

        var (dx, dy, dz, _) = Faces[face];
        var light = snapshot.GetLight(x + dx, y + dy, z + dz);
        return new CubeFaceSignature(
            IsVisible: true,
            Block: block,
            Light: Math.Max(light.Sky, light.Block),
            Ao0: GetCornerAo(snapshot, x, y, z, face, 0),
            Ao1: GetCornerAo(snapshot, x, y, z, face, 1),
            Ao2: GetCornerAo(snapshot, x, y, z, face, 2),
            Ao3: GetCornerAo(snapshot, x, y, z, face, 3));
    }

    private static byte GetCornerAo(ChunkMeshSnapshot snapshot, int x, int y, int z, int face, int corner)
    {
        var offsets = CornerAoOffsets[face][corner];
        var occlusion = 0;
        if (IsOpaque(snapshot, x + offsets[0].X, y + offsets[0].Y, z + offsets[0].Z)) occlusion++;
        if (IsOpaque(snapshot, x + offsets[1].X, y + offsets[1].Y, z + offsets[1].Z)) occlusion++;
        if (IsOpaque(snapshot, x + offsets[2].X, y + offsets[2].Y, z + offsets[2].Z)) occlusion++;
        return (byte)occlusion;
    }

    private static (int X, int Y, int Z) GetFaceBlockPosition(int face, int plane, int u, int v) => face switch
    {
        0 or 1 => (u, Chunk.MinY + plane, v),
        2 or 3 => (u, Chunk.MinY + v, plane),
        4 or 5 => (plane, Chunk.MinY + v, u),
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static (int PlaneCount, int Width, int Height) GetFaceDimensions(int face) => face switch
    {
        // 顶/底面：每个 Y 平面上的 XZ 掩码。
        0 or 1 => (Chunk.SizeY, Chunk.SizeX, Chunk.SizeZ),
        // 前/后面：每个 Z 平面上的 XY 掩码。
        2 or 3 => (Chunk.SizeZ, Chunk.SizeX, Chunk.SizeY),
        // 左/右面：每个 X 平面上的 ZY 掩码。
        4 or 5 => (Chunk.SizeX, Chunk.SizeZ, Chunk.SizeY),
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static void PopulateCubeFaceMask(
        ChunkMeshSnapshot snapshot,
        CubeFaceSignature[] mask,
        int face,
        int plane)
    {
        var dimensions = GetFaceDimensions(face);
        mask.AsSpan(0, checked(dimensions.Width * dimensions.Height)).Clear();
        for (var v = 0; v < dimensions.Height; v++)
        {
            for (var u = 0; u < dimensions.Width; u++)
            {
                var (x, y, z) = GetFaceBlockPosition(face, plane, u, v);
                mask[v * dimensions.Width + u] = CreateCubeFaceSignature(snapshot, x, y, z, face);
            }
        }
    }

    private static (int Width, int Height) ConsumeGreedyRectangle(
        CubeFaceSignature[] mask,
        int maskWidth,
        int maskHeight,
        int u,
        int v,
        CubeFaceSignature signature)
    {
        var width = 1;
        while (u + width < maskWidth && mask[v * maskWidth + u + width] == signature)
        {
            width++;
        }

        var height = 1;
        while (v + height < maskHeight)
        {
            var matches = true;
            for (var rowU = 0; rowU < width; rowU++)
            {
                if (mask[(v + height) * maskWidth + u + rowU] != signature)
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                break;
            }

            height++;
        }

        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                mask[(v + row) * maskWidth + u + column] = default;
            }
        }

        return (width, height);
    }

    /// <summary>
    /// 只扫描一次立方体面并保留合并结果，后续直接写出顶点，避免计数和输出各做一次相同的 AO/光照扫描。
    /// </summary>
    private static GreedyCubeQuad[] CollectGreedyCubeQuads(
        ChunkMeshSnapshot snapshot,
        CancellationToken cancellationToken,
        out int quadCount)
    {
        var quads = ArrayPool<GreedyCubeQuad>.Shared.Rent(1024);
        quadCount = 0;
        var mask = new CubeFaceSignature[Math.Max(
            Chunk.SizeX * Chunk.SizeZ,
            Math.Max(Chunk.SizeX * Chunk.SizeY, Chunk.SizeZ * Chunk.SizeY))];
        try
        {
            for (var face = 0; face < Faces.Length; face++)
            {
                var dimensions = GetFaceDimensions(face);
                for (var plane = 0; plane < dimensions.PlaneCount; plane++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PopulateCubeFaceMask(snapshot, mask, face, plane);
                    for (var v = 0; v < dimensions.Height; v++)
                    {
                        for (var u = 0; u < dimensions.Width; u++)
                        {
                            var signature = mask[v * dimensions.Width + u];
                            if (!signature.IsVisible)
                            {
                                continue;
                            }

                            var (width, height) = ConsumeGreedyRectangle(mask, dimensions.Width, dimensions.Height, u, v, signature);
                            EnsureGreedyCubeQuadCapacity(ref quads, quadCount);
                            var (x, y, z) = GetFaceBlockPosition(face, plane, u, v);
                            quads[quadCount++] = new GreedyCubeQuad(signature, face, x, y, z, width, height);
                        }
                    }
                }
            }

            return quads;
        }
        catch
        {
            ArrayPool<GreedyCubeQuad>.Shared.Return(quads, clearArray: false);
            throw;
        }
    }

    private static void EnsureGreedyCubeQuadCapacity(ref GreedyCubeQuad[] quads, int count)
    {
        if (count < quads.Length)
        {
            return;
        }

        var expanded = ArrayPool<GreedyCubeQuad>.Shared.Rent(checked(quads.Length * 2));
        quads.AsSpan(0, count).CopyTo(expanded);
        ArrayPool<GreedyCubeQuad>.Shared.Return(quads, clearArray: false);
        quads = expanded;
    }

    /// <summary>第一遍统计合并后的四边形，避免热路径 List 扩容和 ToArray 临时复制。</summary>
    private static (int Cross, int Fluid) CountMeshQuads(ChunkMeshSnapshot snapshot, CancellationToken cancellationToken)
    {
        var crossQuadCount = 0;
        var fluidQuadCount = 0;
        for (var y = Chunk.MinY; y < Chunk.MaxYExclusive; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var z = 0; z < Chunk.SizeZ; z++)
            {
                for (var x = 0; x < Chunk.SizeX; x++)
                {
                    var block = snapshot.GetBlock(x, y, z);
                    if (!BlockRegistry.IsRenderable(block))
                    {
                        continue;
                    }

                    var definition = BlockRegistry.Get(block);
                    if (definition.RenderShape == BlockRenderShape.Fluid)
                    {
                        for (var face = 0; face < Faces.Length; face++)
                        {
                            if (IsVisibleFluidFace(snapshot, block, x, y, z, face))
                            {
                                fluidQuadCount++;
                            }
                        }

                        continue;
                    }

                    if (definition.RenderShape == BlockRenderShape.Cross)
                    {
                        crossQuadCount += 4;
                        continue;
                    }
                }
            }
        }

        return (crossQuadCount, fluidQuadCount);
    }

    public static ChunkMeshLayers Mesh(
        ChunkMeshSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var greedyCubeQuads = CollectGreedyCubeQuads(snapshot, cancellationToken, out var greedyCubeQuadCount);
        try
        {
            var (crossQuadCount, fluidQuadCount) = CountMeshQuads(snapshot, cancellationToken);
            var quadCount = checked(greedyCubeQuadCount + crossQuadCount);
            var vertices = new float[checked(quadCount * 4 * 7)];
            var indices = new uint[checked(quadCount * 6)];
            var vertexOffset = 0;
            var indexOffset = 0;
            var fluidVertices = new float[checked(fluidQuadCount * 4 * 7)];
            var fluidIndices = new uint[checked(fluidQuadCount * 6)];
            var fluidVertexOffset = 0;
            var fluidIndexOffset = 0;

            for (var quadIndex = 0; quadIndex < greedyCubeQuadCount; quadIndex++)
            {
                var quad = greedyCubeQuads[quadIndex];
                AppendGreedyCubeQuad(
                    quad.Signature,
                    quad.Face,
                    quad.X,
                    quad.Y,
                    quad.Z,
                    quad.Width,
                    quad.Height,
                    ref vertexOffset,
                    ref indexOffset,
                    vertices,
                    indices);
            }

            for (var y = Chunk.MinY; y < Chunk.MaxYExclusive; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var z = 0; z < Chunk.SizeZ; z++)
                {
                    for (var x = 0; x < Chunk.SizeX; x++)
                    {
                        var block = snapshot.GetBlock(x, y, z);
                        if (!BlockRegistry.IsRenderable(block))
                        {
                            continue;
                        }

                        var definition = BlockRegistry.Get(block);
                        if (definition.RenderShape == BlockRenderShape.Fluid)
                        {
                            for (var face = 0; face < Faces.Length; face++)
                            {
                                if (IsVisibleFluidFace(snapshot, block, x, y, z, face))
                                {
                                    AppendFluidQuad(
                                        snapshot,
                                        block,
                                        x,
                                        y,
                                        z,
                                        face,
                                        ref fluidVertexOffset,
                                        ref fluidIndexOffset,
                                        fluidVertices,
                                        fluidIndices);
                                }
                            }

                            continue;
                        }

                        if (definition.RenderShape == BlockRenderShape.Cross)
                        {
                            AppendCrossQuads(snapshot, block, x, y, z, ref vertexOffset, ref indexOffset, vertices, indices);
                            continue;
                        }
                    }
                }
            }

            if (vertexOffset != vertices.Length || indexOffset != indices.Length ||
                fluidVertexOffset != fluidVertices.Length || fluidIndexOffset != fluidIndices.Length)
            {
                throw new InvalidOperationException("区块网格四边形统计与写入数量不一致");
            }

            return new ChunkMeshLayers(
                new ChunkMeshData(vertices, indices),
                new ChunkMeshData(fluidVertices, fluidIndices));
        }
        finally
        {
            ArrayPool<GreedyCubeQuad>.Shared.Return(greedyCubeQuads, clearArray: false);
        }
    }

    private static void AppendGreedyCubeQuad(
        CubeFaceSignature signature,
        int face,
        int x,
        int y,
        int z,
        int width,
        int height,
        ref int vertexOffset,
        ref int indexOffset,
        float[] vertices,
        uint[] indices)
    {
        var (_, _, _, faceShade) = Faces[face];
        var lightFactor = signature.Light / 15f;
        var shade = faceShade * (UnlitAmbient + (1f - UnlitAmbient) * lightFactor);
        var tile = TextureAtlas.GetTile(signature.Block.Id, face);
        var baseIndex = (uint)(vertexOffset / VertexStride);
        for (var cornerIndex = 0; cornerIndex < FaceCorners[face].Length; cornerIndex++)
        {
            var (cornerX, cornerY, cornerZ, cornerU, cornerV) = FaceCorners[face][cornerIndex];
            var vertexX = face switch
            {
                0 or 1 or 2 or 3 => x + cornerX * width,
                4 or 5 => x + cornerX,
                _ => throw new ArgumentOutOfRangeException(nameof(face)),
            };
            var vertexY = face switch
            {
                0 or 1 => y + cornerY,
                2 or 3 or 4 or 5 => y + cornerY * height,
                _ => throw new ArgumentOutOfRangeException(nameof(face)),
            };
            var vertexZ = face switch
            {
                0 or 1 => z + cornerZ * height,
                2 or 3 => z + cornerZ,
                4 or 5 => z + cornerZ * width,
                _ => throw new ArgumentOutOfRangeException(nameof(face)),
            };
            vertices[vertexOffset++] = vertexX;
            vertices[vertexOffset++] = vertexY;
            vertices[vertexOffset++] = vertexZ;
            vertices[vertexOffset++] = cornerU * width;
            vertices[vertexOffset++] = cornerV * height;
            vertices[vertexOffset++] = shade * (1f - 0.25f * signature.GetAo(cornerIndex));
            vertices[vertexOffset++] = tile;
        }

        indices[indexOffset++] = baseIndex;
        indices[indexOffset++] = baseIndex + 1;
        indices[indexOffset++] = baseIndex + 2;
        indices[indexOffset++] = baseIndex;
        indices[indexOffset++] = baseIndex + 2;
        indices[indexOffset++] = baseIndex + 3;
    }

    private static void AppendFluidQuad(
        ChunkMeshSnapshot snapshot,
        BlockState block,
        int x,
        int y,
        int z,
        int face,
        ref int vertexOffset,
        ref int indexOffset,
        float[] vertices,
        uint[] indices)
    {
        var (dx, dy, dz, shade) = Faces[face];
        var light = snapshot.GetLight(x + dx, y + dy, z + dz);
        var lightFactor = MathF.Max(light.Sky, light.Block) / 15f;
        var vertexShade = shade * (UnlitAmbient + (1f - UnlitAmbient) * lightFactor);
        var tile = TextureAtlas.GetTile(block, face);
        var baseIndex = (uint)(vertexOffset / VertexStride);
        var surfaceHeight = VoxelFluidState.TryDecode(block, out var fluid)
            ? FluidSurfaceHeight(fluid)
            : 1f;
        foreach (var (cornerX, cornerY, cornerZ, cornerU, cornerV) in FaceCorners[face])
        {
            vertices[vertexOffset++] = x + cornerX;
            vertices[vertexOffset++] = y + (cornerY == 0 ? 0f : surfaceHeight - FluidSurfaceInset);
            vertices[vertexOffset++] = z + cornerZ;
            vertices[vertexOffset++] = cornerU;
            vertices[vertexOffset++] = cornerV;
            vertices[vertexOffset++] = vertexShade;
            vertices[vertexOffset++] = tile;
        }

        indices[indexOffset++] = baseIndex;
        indices[indexOffset++] = baseIndex + 1;
        indices[indexOffset++] = baseIndex + 2;
        indices[indexOffset++] = baseIndex;
        indices[indexOffset++] = baseIndex + 2;
        indices[indexOffset++] = baseIndex + 3;
    }

    private static void AppendCrossQuads(
        ChunkMeshSnapshot snapshot,
        ushort block,
        int x,
        int y,
        int z,
        ref int vertexOffset,
        ref int indexOffset,
        float[] vertices,
        uint[] indices)
    {
        var tile = TextureAtlas.GetTile(block, 0);
        var light = snapshot.GetLight(x, y + 1, z);
        var shade = MathF.Max(light.Sky, light.Block) / 15f;
        var vertexShade = UnlitAmbient + (1f - UnlitAmbient) * shade;

        AppendCrossQuad(
            x, y, z, tile, vertexShade,
            ref vertexOffset, ref indexOffset, vertices, indices, reverseWinding: false,
            (CrossMin, 0f, CrossMin), (CrossMax, 0f, CrossMax),
            (CrossMax, 1f, CrossMax), (CrossMin, 1f, CrossMin));
        AppendCrossQuad(
            x, y, z, tile, vertexShade,
            ref vertexOffset, ref indexOffset, vertices, indices, reverseWinding: true,
            (CrossMin, 0f, CrossMin), (CrossMax, 0f, CrossMax),
            (CrossMax, 1f, CrossMax), (CrossMin, 1f, CrossMin));
        AppendCrossQuad(
            x, y, z, tile, vertexShade,
            ref vertexOffset, ref indexOffset, vertices, indices, reverseWinding: false,
            (CrossMin, 0f, CrossMax), (CrossMax, 0f, CrossMin),
            (CrossMax, 1f, CrossMin), (CrossMin, 1f, CrossMax));
        AppendCrossQuad(
            x, y, z, tile, vertexShade,
            ref vertexOffset, ref indexOffset, vertices, indices, reverseWinding: true,
            (CrossMin, 0f, CrossMax), (CrossMax, 0f, CrossMin),
            (CrossMax, 1f, CrossMin), (CrossMin, 1f, CrossMax));
    }

    private static void AppendCrossQuad(
        int x,
        int y,
        int z,
        int tile,
        float vertexShade,
        ref int vertexOffset,
        ref int indexOffset,
        float[] vertices,
        uint[] indices,
        bool reverseWinding,
        (float X, float Y, float Z) first,
        (float X, float Y, float Z) second,
        (float X, float Y, float Z) third,
        (float X, float Y, float Z) fourth)
    {
        var baseIndex = (uint)(vertexOffset / VertexStride);
        AppendCrossVertex(x, y, z, first, 0f, 1f, tile, vertexShade, ref vertexOffset, vertices);
        AppendCrossVertex(x, y, z, second, 1f, 1f, tile, vertexShade, ref vertexOffset, vertices);
        AppendCrossVertex(x, y, z, third, 1f, 0f, tile, vertexShade, ref vertexOffset, vertices);
        AppendCrossVertex(x, y, z, fourth, 0f, 0f, tile, vertexShade, ref vertexOffset, vertices);
        if (reverseWinding)
        {
            indices[indexOffset++] = baseIndex;
            indices[indexOffset++] = baseIndex + 2;
            indices[indexOffset++] = baseIndex + 1;
            indices[indexOffset++] = baseIndex;
            indices[indexOffset++] = baseIndex + 3;
            indices[indexOffset++] = baseIndex + 2;
        }
        else
        {
            indices[indexOffset++] = baseIndex;
            indices[indexOffset++] = baseIndex + 1;
            indices[indexOffset++] = baseIndex + 2;
            indices[indexOffset++] = baseIndex;
            indices[indexOffset++] = baseIndex + 2;
            indices[indexOffset++] = baseIndex + 3;
        }
    }

    private static void AppendCrossVertex(
        int x,
        int y,
        int z,
        (float X, float Y, float Z) point,
        float u,
        float v,
        int tile,
        float vertexShade,
        ref int vertexOffset,
        float[] vertices)
    {
        vertices[vertexOffset++] = x + point.X;
        vertices[vertexOffset++] = y + point.Y;
        vertices[vertexOffset++] = z + point.Z;
        vertices[vertexOffset++] = u;
        vertices[vertexOffset++] = v;
        vertices[vertexOffset++] = vertexShade;
        vertices[vertexOffset++] = tile;
    }
}
