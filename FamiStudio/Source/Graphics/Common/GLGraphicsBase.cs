﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FamiStudio
{
    public abstract class GraphicsBase : IDisposable
    {
        protected bool builtAtlases;
        protected int lineWidthBias;
        protected float[] viewportScaleBias = new float[4];
        protected Rectangle screenRect;
        protected Rectangle screenRectFlip;
        protected TransformStack transform = new TransformStack();
        protected Bitmap dashedBitmap;
        protected Dictionary<int, BitmapAtlas> atlases = new Dictionary<int, BitmapAtlas>();
        protected List<ClipRegion> clipRegions = new List<ClipRegion>();
        protected Stack<ClipRegion> clipStack = new Stack<ClipRegion>();
        protected CommandList[] layerCommandLists = new CommandList[(int)GraphicsLayer.Count];
        protected byte curDepthValue = 0x80; // -128
        protected byte maxDepthValue = 0x80; // -128
        protected Color clearColor;

        protected struct ClipRegion
        {
            public RectangleF rect;
            public byte depthValue;
        }

        public byte DepthValue => curDepthValue;
        public int DashTextureSize => dashedBitmap.Size.Width;
        public TransformStack Transform => transform;
        public RectangleF CurrentClipRegion => clipStack.Peek().rect;

        protected const int MaxAtlasResolution = 1024;
        protected const int MaxVertexCount = 160 * 1024;
        protected const int MaxIndexCount = MaxVertexCount / 4 * 6;

        // These are only used temporarily during text/bmp rendering.
        protected float[] vtxArray = new float[MaxVertexCount * 2];
        protected float[] texArray = new float[MaxVertexCount * 2];
        protected int[]   colArray = new int[MaxVertexCount];
        protected byte[]  depArray = new byte[MaxVertexCount];

        protected short[] quadIdxArray = new short[MaxIndexCount];

        protected List<float[]> freeVertexArrays = new List<float[]>();
        protected List<byte[]>  freeByteArrays = new List<byte[]>();
        protected List<int[]>   freeColorArrays  = new List<int[]>();
        protected List<short[]> freeIndexArrays  = new List<short[]>();

        protected abstract int CreateEmptyTexture(int width, int height, bool alpha, bool filter);
        protected abstract int CreateTexture(SimpleBitmap bmp, bool filter);
        protected abstract void Clear();
        protected abstract void DrawCommandList(CommandList list, bool depthTest);
        protected abstract void DrawDepthPrepass();
        protected abstract string GetScaledFilename(string name, out bool needsScaling);
        protected abstract BitmapAtlas CreateBitmapAtlasFromResources(string[] names);
        public abstract void DeleteTexture(int id);

        protected const string AtlasPrefix = "FamiStudio.Resources.Atlas.";

        public CommandList BackgroundCommandList => GetCommandList(GraphicsLayer.Background);
        public CommandList DefaultCommandList    => GetCommandList(GraphicsLayer.Default);
        public CommandList ForegroundCommandList => GetCommandList(GraphicsLayer.Foreground);
        public CommandList OverlayCommandList    => GetCommandList(GraphicsLayer.Overlay);

        protected GraphicsBase()
        {
            // Quad index buffer.
            for (int i = 0, j = 0; i < MaxVertexCount; i += 4)
            {
                var i0 = (short)(i + 0);
                var i1 = (short)(i + 1);
                var i2 = (short)(i + 2);
                var i3 = (short)(i + 3);

                quadIdxArray[j++] = i0;
                quadIdxArray[j++] = i1;
                quadIdxArray[j++] = i2;
                quadIdxArray[j++] = i0;
                quadIdxArray[j++] = i2;
                quadIdxArray[j++] = i3;
            }

            // HACK : If we are on android with a scaling of 1.0, this mean we are
            // rendering a video and we dont need any of the atlases.
            if (!Platform.IsAndroid || DpiScaling.Window != 1.0f)
                BuildBitmapAtlases();
        }

        public virtual void BeginDrawFrame(Rectangle rect, Color clear)
        {
            Debug.Assert(transform.IsEmpty);
            Debug.Assert(clipStack.Count == 0);

            clearColor = clear;
            clipRegions.Clear();

            lineWidthBias = 0;
            screenRect = rect;
            screenRectFlip = FlipRectangleY(rect, rect.Height); // MATTT Clean that up.
            transform.SetIdentity();
            curDepthValue = 0x80;
            maxDepthValue = 0x80;

            viewportScaleBias[0] =  2.0f / screenRect.Width;
            viewportScaleBias[1] = -2.0f / screenRect.Height;
            viewportScaleBias[2] = -1.0f;
            viewportScaleBias[3] =  1.0f;
        }

        public virtual void EndDrawFrame()
        {
            Debug.Assert(transform.IsEmpty);
            Debug.Assert(clipStack.Count == 0);

            Clear();
            DrawDepthPrepass();

            for (int i = 0; i < layerCommandLists.Length; i++)
            {
                if (layerCommandLists[i] != null)
                { 
                    DrawCommandList(layerCommandLists[i], i != (int)GraphicsLayer.Overlay);
                }
            }

            for (int i = 0; i < layerCommandLists.Length; i++)
            {
                if (layerCommandLists[i] != null)
                {
                    layerCommandLists[i].Release();
                    layerCommandLists[i] = null;
                }
            }
        }

        public virtual void PushClipRegion(Point p, Size s)
        {
            PushClipRegion(p.X, p.Y, s.Width, s.Height);
        }

        public virtual void PushClipRegion(float x, float y, float width, float height)
        {
            var ox = x;
            var oy = y;

            transform.TransformPoint(ref ox, ref oy);

            maxDepthValue = (byte)((maxDepthValue + 1) & 0xff);
            curDepthValue = maxDepthValue;

            var clip = new ClipRegion();

            clip.rect = new RectangleF(ox, oy, width, height);
            clip.depthValue = curDepthValue;

            if (clipStack.Count > 0)
                clip.rect = RectangleF.Intersect(clip.rect, clipStack.Peek().rect);

            clipRegions.Add(clip);
            clipStack.Push(clip);
        }

        public virtual void PopClipRegion()
        {
            var region = clipStack.Pop();
            curDepthValue = clipStack.Count > 0 ? clipStack.Peek().depthValue : (byte)0x80;
        }

        public CommandList GetCommandList(GraphicsLayer layer = GraphicsLayer.Default)
        {
            var idx = (int)layer;

            if (layerCommandLists[idx] == null)
            {
                layerCommandLists[idx] = new CommandList(this, dashedBitmap.Size.Width);
            }

            return layerCommandLists[idx];
        }

        protected SimpleBitmap LoadBitmapFromResourceWithScaling(string name)
        {
            var scaledFilename = GetScaledFilename(name, out var needsScaling);
            var bmp = TgaFile.LoadFromResource(scaledFilename, true);

            // Pre-resize all images so we dont have to deal with scaling later.
            if (needsScaling)
            {
                var newWidth  = Math.Max(1, (int)(bmp.Width  * (DpiScaling.Window / 2.0f)));
                var newHeight = Math.Max(1, (int)(bmp.Height * (DpiScaling.Window / 2.0f)));

                bmp = bmp.Resize(newWidth, newHeight);
            }

            return bmp;
        }

        private void BuildBitmapAtlases()
        {
            // Build atlases.
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();
            var atlasImages = new Dictionary<int, List<string>>();
            var filteredImages = new HashSet<string>();

            foreach (var res in resourceNames)
            {
                if (res.StartsWith(AtlasPrefix))
                {
                    // Remove any scaling from the name.
                    var at = res.IndexOf('@');
                    var cleanedFilename = res.Substring(AtlasPrefix.Length, at >= 0 ? at - AtlasPrefix.Length : res.Length - AtlasPrefix.Length - 4);
                    filteredImages.Add(cleanedFilename);
                }
            }

            // Keep 1 atlas per power-of-two size. 
            var minWidth = DpiScaling.ScaleForWindow(16);

            foreach (var res in filteredImages)
            {
                var scaledFilename = GetScaledFilename(AtlasPrefix + res, out var needsScaling);
                TgaFile.GetResourceImageSize(scaledFilename, out var width, out var height);

                if (needsScaling)
                {
                    width  = Math.Max(1, (int)(width  * (DpiScaling.Window / 2.0f)));
                    height = Math.Max(1, (int)(height * (DpiScaling.Window / 2.0f)));
                }

                width  = Math.Max(minWidth, width);
                height = Math.Max(minWidth, height);

                var maxSize = Math.Max(width, height);
                var maxSizePow2 = Utils.NextPowerOfTwo(maxSize);

                if (!atlasImages.TryGetValue(maxSizePow2, out var atlas))
                {
                    atlas = new List<string>();
                    atlasImages.Add(maxSizePow2, atlas);
                }

                atlas.Add(res);
            }

            // Build the textures.
            foreach (var kv in atlasImages)
            {
                var bmp = CreateBitmapAtlasFromResources(kv.Value.ToArray());
                atlases.Add(kv.Key, bmp);
            }

            builtAtlases = true;
        }

        public BitmapAtlasRef GetBitmapAtlasRef(string name)
        {
            // Look in all atlases
            foreach (var a in atlases.Values)
            {
                var idx = a.GetElementIndex(name);
                if (idx >= 0)
                    return new BitmapAtlasRef(a, idx);
            }

            Debug.Assert(false); // Not found!
            return null;
        }

        public BitmapAtlasRef[] GetBitmapAtlasRefs(string[] name, string prefix = null)
        {
            var refs = new BitmapAtlasRef[name.Length];
            for (int i = 0; i < refs.Length; i++)
                refs[i] = GetBitmapAtlasRef(prefix != null ? prefix + name[i] : name[i]);
            return refs;
        }

        public void SetLineBias(int bias)
        {
            lineWidthBias = bias;
        }

        protected virtual CommandList CreateCommandList()
        {
            return new CommandList(this, dashedBitmap.Size.Width, lineWidthBias);
        }

        protected Rectangle FlipRectangleY(Rectangle rc, int sizeY)
        {
            return new Rectangle(rc.Left, sizeY - rc.Top - rc.Height, rc.Width, rc.Height);
        }

        public float MeasureString(string text, Font font, bool mono = false)
        {
            return font.MeasureString(text, mono);
        }

        public Bitmap CreateEmptyBitmap(int width, int height, bool alpha, bool filter)
        {
            return new Bitmap(this, CreateEmptyTexture(width, height, alpha, filter), width, height, true, filter);
        }

        protected T ReadFontParam<T>(string[] values, string key)
        {
            for (int i = 1; i < values.Length; i += 2)
            {
                if (values[i] == key)
                {
                    return (T)Convert.ChangeType(values[i + 1], typeof(T));
                }
            }

            Debug.Assert(false);
            return default(T);
        }

        public Font CreateScaledFont(Font source, int desiredHeight)
        {
            return null;
        }

        public Font CreateFontFromResource(string name, bool bold, int size)
        {
            var suffix = bold ? "Bold" : "";
            var basename = $"{name}{size}{suffix}";
            var fntfile = $"FamiStudio.Resources.Fonts.{basename}.fnt";
            var imgfile = $"FamiStudio.Resources.Fonts.{basename}_0.tga";

            var str = "";
            using (Stream stream = typeof(GraphicsBase).Assembly.GetManifestResourceStream(fntfile))
            using (StreamReader reader = new StreamReader(stream))
            {
                str = reader.ReadToEnd();
            }

            var lines = str.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var bmp = TgaFile.LoadFromResource(imgfile, true);

            var font = (Font)null;

            int baseValue = 0;
            int lineHeight = 0;
            int texSizeX = 256;
            int texSizeY = 256;

            foreach (var line in lines)
            {
                var splits = line.Split(new[] { ' ', '=', '\"' }, StringSplitOptions.RemoveEmptyEntries);

                switch (splits[0])
                {
                    case "common":
                    {
                        baseValue = ReadFontParam<int>(splits, "base");
                        lineHeight = ReadFontParam<int>(splits, "lineHeight");
                        texSizeX = ReadFontParam<int>(splits, "scaleW");
                        texSizeY = ReadFontParam<int>(splits, "scaleH");
                        font = new Font(this, CreateTexture(bmp, false), size, baseValue, lineHeight);
                        break;
                    }
                    case "char":
                    {
                        var charInfo = new Font.CharInfo();

                        int c = ReadFontParam<int>(splits, "id");
                        int x = ReadFontParam<int>(splits, "x");
                        int y = ReadFontParam<int>(splits, "y");

                        charInfo.width = ReadFontParam<int>(splits, "width");
                        charInfo.height = ReadFontParam<int>(splits, "height");
                        charInfo.xoffset = ReadFontParam<int>(splits, "xoffset");
                        charInfo.yoffset = ReadFontParam<int>(splits, "yoffset");
                        charInfo.xadvance = ReadFontParam<int>(splits, "xadvance");
                        charInfo.u0 = (x + 0.0f) / (float)texSizeX;
                        charInfo.v0 = (y + 0.0f) / (float)texSizeY;
                        charInfo.u1 = (x + 0.0f + charInfo.width) / (float)texSizeX;
                        charInfo.v1 = (y + 0.0f + charInfo.height) / (float)texSizeY;

                        font.AddChar((char)c, charInfo);

                        break;
                    }
                    case "kerning":
                    {
                        int c0 = ReadFontParam<int>(splits, "first");
                        int c1 = ReadFontParam<int>(splits, "second");
                        int amount = ReadFontParam<int>(splits, "amount");
                        font.AddKerningPair(c0, c1, amount);
                        break;
                    }
                }
            }

            return font;
        }

        public virtual void Dispose()
        {
            foreach (var a in atlases.Values)
                a.Dispose();

            atlases.Clear();
        }

        public float[] GetVertexArray()
        {
            if (freeVertexArrays.Count > 0)
            {
                var lastIdx = freeVertexArrays.Count - 1;
                var arr = freeVertexArrays[lastIdx];
                freeVertexArrays.RemoveAt(lastIdx);
                return arr;
            }
            else
            {
                return new float[MaxVertexCount * 2];
            }
        }

        public byte[] GetByteArray()
        {
            if (freeByteArrays.Count > 0)
            {
                var lastIdx = freeByteArrays.Count - 1;
                var arr = freeByteArrays[lastIdx];
                freeByteArrays.RemoveAt(lastIdx);
                return arr;
            }
            else
            {
                return new byte[MaxVertexCount];
            }
        }

        public int[] GetColorArray()
        {
            if (freeColorArrays.Count > 0)
            {
                var lastIdx = freeColorArrays.Count - 1;
                var arr = freeColorArrays[lastIdx];
                freeColorArrays.RemoveAt(lastIdx);
                return arr;
            }
            else
            {
                return new int[MaxVertexCount];
            }
        }

        public short[] GetIndexArray()
        {
            if (freeIndexArrays.Count > 0)
            {
                var lastIdx = freeIndexArrays.Count - 1;
                var arr = freeIndexArrays[lastIdx];
                freeIndexArrays.RemoveAt(lastIdx);
                return arr;
            }
            else
            {
                return new short[MaxVertexCount];
            }
        }

        public void ReleaseVertexArray(float[] a)
        {
            freeVertexArrays.Add(a);
        }


        public void ReleaseByteArray(byte[] a)
        {
            freeByteArrays.Add(a);
        }

        public void ReleaseColorArray(int[] a)
        {
            freeColorArrays.Add(a);
        }

        public void ReleaseIndexArray(short[] a)
        {
            freeIndexArrays.Add(a);
        }
    };

    public enum GraphicsLayer
    {
        Background,
        Default,
        Foreground,
        Overlay,
        Count
    };

    public class Font : IDisposable
    {
        public class CharInfo
        {
            public int width;
            public int height;
            public int xoffset;
            public int yoffset;
            public int xadvance;
            public float u0;
            public float v0;
            public float u1;
            public float v1;
        }

        Dictionary<char, CharInfo> charMap = new Dictionary<char, CharInfo>();
        Dictionary<int, int> kerningPairs = new Dictionary<int, int>();

        private int texture;
        private int size;
        private int baseValue;
        private int lineHeight;
        private GraphicsBase graphics;

        public int Texture => texture;
        public int Size => size;
        public int LineHeight => lineHeight;
        public int OffsetY => size - baseValue;

        public Font(GraphicsBase g, int tex, int sz, int b, int l)
        {
            graphics = g;
            texture = tex;
            size = sz;
            baseValue = b;
            lineHeight = l;
        }

        public void Dispose()
        {
            graphics.DeleteTexture(texture);
            texture = -1;
        }

        public void AddChar(char c, CharInfo info)
        {
            charMap[c] = info;
        }

        public void AddKerningPair(int c0, int c1, int amount)
        {
            kerningPairs[c0 | (c1 << 8)] = amount;
        }

        public CharInfo GetCharInfo(char c, bool fallback = true)
        {
            if (charMap.TryGetValue(c, out CharInfo info))
            {
                return info;
            }
            else if (fallback)
            {
                return charMap['?'];
            }
            else
            {
                return null;
            }
        }

        public int GetKerning(char c0, char c1)
        {
            int key = (int)c0 | ((int)c1 << 8);
            return kerningPairs.TryGetValue(key, out int amount) ? amount : 0;
        }

        public bool TruncateString(ref string text, int maxSizeX)
        {
            int x = 0;

            for (int i = 0; i < text.Length; i++)
            {
                var c0 = text[i];
                var info = GetCharInfo(c0);

                int x0 = x + info.xoffset;
                int x1 = x0 + info.width;

                if (x1 >= maxSizeX)
                {
                    text = text.Substring(0, i);
                    return true;
                }

                x += info.xadvance;
                if (i != text.Length - 1)
                {
                    char c1 = text[i + 1];
                    x += GetKerning(c0, c1);
                }
            }

            return false;
        }

        public int GetNumCharactersForSize(string text, int sizeX, bool canRoundUp = false)
        {
            var x = 0;
            var maxX = 0;

            for (int i = 0; i < text.Length; i++)
            {
                var c0 = text[i];
                var info = GetCharInfo(c0);

                int x0 = x + info.xoffset;
                int x1 = x0 + info.width;

                maxX = Math.Max(maxX, x1);

                if (maxX > sizeX)
                {
                    if (canRoundUp && sizeX >= (x1 + x0) / 2)
                        return i + 1;
                    else
                        return i;
                }

                x += info.xadvance;
                if (i != text.Length - 1)
                {
                    char c1 = text[i + 1];
                    x += GetKerning(c0, c1);
                }
            }

            return text.Length;
        }


        public int MeasureString(string text, bool mono)
        {
            int x = 0;

            if (mono)
            {
                var info = GetCharInfo('0');

                for (int i = 0; i < text.Length; i++)
                {
                    x += info.xadvance;
                }
            }
            else
            {
                for (int i = 0; i < text.Length; i++)
                {
                    var c0 = text[i];
                    var info = GetCharInfo(c0);

                    x += info.xadvance;
                    if (i != text.Length - 1)
                    {
                        char c1 = text[i + 1];
                        x += GetKerning(c0, c1);
                    }
                }
            }

            return x;
        }
    }

    public class Bitmap : IDisposable
    {
        protected int id;
        protected Size size;
        protected bool dispose = true;
        protected bool filter = false;
        protected bool atlas = false;
        protected GraphicsBase graphics;

        public int Id => id;
        public Size Size => size;
        public bool Filtering => filter;
        public bool IsAtlas => atlas;

        public Bitmap(GraphicsBase g, int id, int width, int height, bool disp = true, bool filter = false)
        {
            this.graphics = g;
            this.id = id;
            this.size = new Size(width, height);
            this.dispose = disp;
            this.filter = filter;
        }

        public void Dispose()
        {
            if (dispose)
                graphics.DeleteTexture(id);
            id = -1;
        }

        public override int GetHashCode()
        {
            return id;
        }
    }

    public class BitmapAtlas : Bitmap
    {
        private string[] elementNames;
        private Rectangle[] elementRects;

        public Size GetElementSize(int index) => elementRects[index].Size;

        public BitmapAtlas(GraphicsBase g, int id, int atlasSizeX, int atlasSizeY, string[] names, Rectangle[] rects, bool filter = false) :
            base(g, id, atlasSizeX, atlasSizeY, true, filter)
        {
            elementNames = names;
            elementRects = rects;
            atlas = true;
        }

        public int GetElementIndex(string name)
        {
            // By the way we build the atlases, elements are sorted by name
            return Array.BinarySearch(elementNames, name);
        }

        public void GetElementUVs(int elementIndex, out float u0, out float v0, out float u1, out float v1)
        {
            var rect = elementRects[elementIndex];

            u0 = rect.Left   / (float)size.Width;
            u1 = rect.Right  / (float)size.Width;
            v0 = rect.Top    / (float)size.Height;
            v1 = rect.Bottom / (float)size.Height;
        }
    }

    public class BitmapAtlasRef
    {
        private BitmapAtlas atlas;
        private int index;

        public BitmapAtlas Atlas => atlas;
        public int ElementIndex => index;
        public Size ElementSize => atlas.GetElementSize(index);

        public BitmapAtlasRef(BitmapAtlas a, int idx)
        {
            atlas = a;
            index = idx;
        }

        public void GetElementUVs(out float u0, out float v0, out float u1, out float v1)
        {
            atlas.GetElementUVs(index, out u0, out v0, out u1, out v1);
        }
    }

    public struct Transform
    {
        public float ScaleX;
        public float ScaleY;
        public float TranslationX;
        public float TranslationY;

        public bool HasScaling => ScaleX != 1.0f || ScaleY != 1.0f;

        public Transform(float sx, float sy, float tx, float ty)
        {
            ScaleX = sx;
            ScaleY = sy;
            TranslationX = tx;
            TranslationY = ty;
        }
    }

    public class TransformStack
    {
        private Transform transform = new Transform(1, 1, 0, 0); // xy = scale, zw = translation
        private Stack<Transform> transformStack = new Stack<Transform>();

        public bool HasScaling => transform.HasScaling;
        public bool IsEmpty => transformStack.Count == 0;

        public void SetIdentity()
        {
            transform.ScaleX = 1;
            transform.ScaleY = 1;
            transform.TranslationX = 0;
            transform.TranslationY = 0;
        }

        public void PushTranslation(float x, float y)
        {
            transformStack.Push(transform);
            transform.TranslationX += x;
            transform.TranslationY += y;
        }

        public void PushTransform(float tx, float ty, float sx, float sy)
        {
            transformStack.Push(transform);

            transform.ScaleX *= sx;
            transform.ScaleY *= sy;
            transform.TranslationX += tx;
            transform.TranslationY += ty;
        }

        public void PopTransform()
        {
            transform = transformStack.Pop();
        }

        public void TransformPoint(ref float x, ref float y)
        {
            x = x * transform.ScaleX + transform.TranslationX;
            y = y * transform.ScaleY + transform.TranslationY;
        }

        public void ReverseTransformPoint(ref float x, ref float y)
        {
            x = (x - transform.TranslationX) / transform.ScaleX;
            y = (y - transform.TranslationY) / transform.ScaleY;
        }

        public void ScaleSize(ref float width, ref float height)
        {
            width  *= transform.ScaleX;
            height *= transform.ScaleY;
        }

        public void GetOrigin(out float x, out float y)
        {
            x = 0;
            y = 0;
            TransformPoint(ref x, ref y);
        }
    }

    [Flags]
    public enum TextFlags
    {
        None = 0,

        HorizontalAlignMask = 0x3,
        VerticalAlignMask   = 0xc,

        Left   = 0 << 0,
        Center = 1 << 0,
        Right  = 2 << 0,

        Top    = 0 << 2,
        Middle = 1 << 2,
        Bottom = 2 << 2,

        TopLeft      = Top    | Left,
        TopCenter    = Top    | Center,
        TopRight     = Top    | Right,
        MiddleLeft   = Middle | Left,
        MiddleCenter = Middle | Center,
        MiddleRight  = Middle | Right,
        BottomLeft   = Bottom | Left,
        BottomCenter = Bottom | Center,
        BottomRight  = Bottom | Right,

        Clip      = 1 << 7,
        Ellipsis  = 1 << 8,
        Monospace = 1 << 9
    }

    // This is common to both OGL, it only does data packing, no GL calls.
    public class CommandList
    {
        private class PolyBatch
        {
            public float[] vtxArray;
            public int[]   colArray;
            public short[] idxArray;
            public byte[]  depArray;

            public int vtxIdx = 0;
            public int colIdx = 0;
            public int idxIdx = 0;
            public int depIdx = 0;
        };

        private class LineBatch
        {
            public float[] vtxArray;
            public float[] texArray;
            public int[]   colArray;
            public byte[]  depArray;

            public int vtxIdx = 0;
            public int texIdx = 0;
            public int colIdx = 0;
            public int depIdx = 0;
        };
        
        private class LineSmoothBatch
        {
            public float[] vtxArray;
            public byte[]  dstArray;
            public int[]   colArray;
            public short[] idxArray;
            public byte[]  depArray;

            public int vtxIdx = 0;
            public int dstIdx = 0;
            public int colIdx = 0;
            public int idxIdx = 0;
            public int depIdx = 0;
        };

        private class TextInstance
        {
            public RectangleF layoutRect;
            public RectangleF clipRect;
            public TextFlags flags;
            public string text;
            public Color color;
            public byte depth;
        };

        private class BitmapInstance
        {
            public float x;
            public float y;
            public float sx;
            public float sy;
            public float u0;
            public float v0;
            public float u1;
            public float v1;
            public float opacity;
            public Color tint;
            public bool rotated;
            public byte depth;
        }

        public class PolyDrawData
        {
            public float[] vtxArray;
            public int[]   colArray;
            public short[] idxArray;
            public byte[]  depArray;

            public int vtxArraySize;
            public int colArraySize;
            public int idxArraySize;
            public int depArraySize;

            public bool smooth;
            public int numIndices;
        };

        public class LineDrawData
        {
            public float[] vtxArray;
            public float[] texArray;
            public int[]   colArray;
            public byte[]  depArray;

            public int vtxArraySize;
            public int texArraySize;
            public int colArraySize;
            public int depArraySize;

            public int numVertices;
            public bool smooth;
            public float lineWidth;
        };

        public class LineSmoothDrawData
        {
            public float[] vtxArray;
            public byte[]  dstArray;
            public int[]   colArray;
            public short[] idxArray;
            public byte[]  depArray;

            public int vtxArraySize;
            public int dstArraySize;
            public int colArraySize;
            public int idxArraySize;
            public int depArraySize;

            public int numIndices;
        };

        public class DrawData
        {
            public int textureId;
            public int start;
            public int count;
        };

        private int lineWidthBias;
        private float invDashTextureSize;
        private PolyBatch polyBatch;
        private LineBatch lineBatch;
        private LineSmoothBatch lineSmoothBatch;

        private Dictionary<Font,   List<TextInstance>>   texts   = new Dictionary<Font,   List<TextInstance>>();
        private Dictionary<Bitmap, List<BitmapInstance>> bitmaps = new Dictionary<Bitmap, List<BitmapInstance>>();

        private GraphicsBase graphics;
        private TransformStack xform;

        public TransformStack Transform => xform;
        public GraphicsBase Graphics => graphics;

        public bool HasAnyPolygons       => polyBatch != null;
        public bool HasAnyLines          => lineBatch != null;
        public bool HasAnySmoothLines    => lineSmoothBatch != null;
        public bool HasAnyTexts          => texts.Count > 0;
        public bool HasAnyBitmaps        => bitmaps.Count > 0;
        public bool HasAnything          => HasAnyPolygons || HasAnyLines || HasAnySmoothLines || HasAnyTexts || HasAnyBitmaps;

        public CommandList(GraphicsBase g, int dashTextureSize, int lineBias = 0)
        {
            graphics = g;
            xform = g.Transform;
            invDashTextureSize = 1.0f / dashTextureSize;
            lineWidthBias = lineBias;
        }

        public void PushTranslation(float x, float y)
        {
            xform.PushTranslation(x, y);
        }

        public void PushTransform(float tx, float ty, float sx, float sy)
        {
            xform.PushTransform(tx, ty, sx, sy);
        }

        public void PopTransform()
        {
            xform.PopTransform();
        }

        public void PushClipRegion(float x, float y, float width, float height)
        {
            graphics.PushClipRegion(x, y, width, height);
        }

        public void PopClipRegion()
        {
            graphics.PopClipRegion();
        }

        public void Release()
        {
            if (polyBatch != null)
            {
                graphics.ReleaseVertexArray(polyBatch.vtxArray);
                graphics.ReleaseColorArray(polyBatch.colArray);
                graphics.ReleaseIndexArray(polyBatch.idxArray);
                graphics.ReleaseByteArray(polyBatch.depArray);
            }

            if (lineBatch != null)
            {
                graphics.ReleaseVertexArray(lineBatch.vtxArray);
                graphics.ReleaseVertexArray(lineBatch.texArray);
                graphics.ReleaseColorArray(lineBatch.colArray);
                graphics.ReleaseByteArray(lineBatch.depArray);
            }

            if (lineSmoothBatch != null)
            {
                graphics.ReleaseVertexArray(lineSmoothBatch.vtxArray);
                graphics.ReleaseByteArray(lineSmoothBatch.dstArray);
                graphics.ReleaseColorArray(lineSmoothBatch.colArray);
                graphics.ReleaseIndexArray(lineSmoothBatch.idxArray);
                graphics.ReleaseByteArray(lineSmoothBatch.depArray);
            }

            polyBatch = null;
            lineBatch = null;
            lineSmoothBatch = null;
        }

        private PolyBatch GetPolygonBatch()
        {
            if (polyBatch == null)
            {
                polyBatch = new PolyBatch();
                polyBatch.vtxArray = graphics.GetVertexArray();
                polyBatch.colArray = graphics.GetColorArray();
                polyBatch.idxArray = graphics.GetIndexArray();
                polyBatch.depArray = graphics.GetByteArray();
            }

            return polyBatch;
        }

        private void DrawLineInternal(float x0, float y0, float x1, float y1, Color color, bool dash)
        {
            if (lineBatch == null)
            {
                lineBatch = new LineBatch();
                lineBatch.vtxArray = graphics.GetVertexArray();
                lineBatch.texArray = graphics.GetVertexArray();
                lineBatch.colArray = graphics.GetColorArray();
                lineBatch.depArray = graphics.GetByteArray();
            }

            var batch = lineBatch;
            var depth = graphics.DepthValue;

            batch.vtxArray[batch.vtxIdx++] = x0;
            batch.vtxArray[batch.vtxIdx++] = y0;
            batch.vtxArray[batch.vtxIdx++] = x1;
            batch.vtxArray[batch.vtxIdx++] = y1;

            if (dash)
            {
                if (x0 == x1)
                {
                    batch.texArray[batch.texIdx++] = 0.5f;
                    batch.texArray[batch.texIdx++] = (y0 + 0.5f) * invDashTextureSize;
                    batch.texArray[batch.texIdx++] = 0.5f;
                    batch.texArray[batch.texIdx++] = (y1 + 0.5f) * invDashTextureSize;
                }
                else
                {
                    batch.texArray[batch.texIdx++] = (x0 + 0.5f) * invDashTextureSize;
                    batch.texArray[batch.texIdx++] = 0.5f;
                    batch.texArray[batch.texIdx++] = (x1 + 0.5f) * invDashTextureSize;
                    batch.texArray[batch.texIdx++] = 0.5f;
                }
            }
            else
            {
                batch.texArray[batch.texIdx++] = 0.5f;
                batch.texArray[batch.texIdx++] = 0.5f;
                batch.texArray[batch.texIdx++] = 0.5f;
                batch.texArray[batch.texIdx++] = 0.5f;
            }

            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();

            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
        }

        private void DrawThickLineInternal(float x0, float y0, float x1, float y1, Color color, int width, bool miter)
        {
            Debug.Assert(width > 1 && width < 100);
            //Debug.Assert((width & 1) == 0); // Odd values are a bit misbehaved.

            if (polyBatch == null)
            {
                polyBatch = new PolyBatch();
                polyBatch.vtxArray = graphics.GetVertexArray();
                polyBatch.colArray = graphics.GetColorArray();
                polyBatch.idxArray = graphics.GetIndexArray();
                polyBatch.depArray = graphics.GetByteArray();
            }

            var batch = polyBatch;
            var depth = graphics.DepthValue;

            var dx = x1 - x0;
            var dy = y1 - y0;
            var invHalfWidth = (width * 0.5f) / (float)Math.Sqrt(dx * dx + dy * dy);
            dx *= invHalfWidth;
            dy *= invHalfWidth;

            if (miter)
            {
                x0 -= dx;
                y0 -= dy;
                x1 += dx;
                y1 += dy;
            }

            var i0 = (short)(batch.vtxIdx / 2 + 0);
            var i1 = (short)(batch.vtxIdx / 2 + 1);
            var i2 = (short)(batch.vtxIdx / 2 + 2);
            var i3 = (short)(batch.vtxIdx / 2 + 3);
 
            batch.idxArray[batch.idxIdx++] = i0;
            batch.idxArray[batch.idxIdx++] = i1;
            batch.idxArray[batch.idxIdx++] = i2;
            batch.idxArray[batch.idxIdx++] = i0;
            batch.idxArray[batch.idxIdx++] = i2;
            batch.idxArray[batch.idxIdx++] = i3;

            batch.vtxArray[batch.vtxIdx++] = x0 - dy;
            batch.vtxArray[batch.vtxIdx++] = y0 + dx;
            batch.vtxArray[batch.vtxIdx++] = x1 - dy;
            batch.vtxArray[batch.vtxIdx++] = y1 + dx;
            batch.vtxArray[batch.vtxIdx++] = x1 + dy;
            batch.vtxArray[batch.vtxIdx++] = y1 - dx;
            batch.vtxArray[batch.vtxIdx++] = x0 + dy;
            batch.vtxArray[batch.vtxIdx++] = y0 - dx;

            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();

            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
        }

        private void DrawThickSmoothLineInternal(float x0, float y0, float x1, float y1, Color color, int width, bool miter)
        {
            Debug.Assert(width < 100);

            // Cant draw nice AA line that are 1 pixel wide.
            width = Math.Max(2, width);

            // Odd values are tricky to make work with rasterization rules.
            // This works OK, but tend to create lines that are a bit see-through.
            var encodedWidth = (byte)width;
            if ((width & 1) != 0)
            {
                width++;
                encodedWidth--;
            }

            if (lineSmoothBatch == null)
            {
                lineSmoothBatch = new LineSmoothBatch();
                lineSmoothBatch.vtxArray = graphics.GetVertexArray();
                lineSmoothBatch.dstArray = graphics.GetByteArray();
                lineSmoothBatch.colArray = graphics.GetColorArray();
                lineSmoothBatch.idxArray = graphics.GetIndexArray();
                lineSmoothBatch.depArray = graphics.GetByteArray();
            }

            var batch = lineSmoothBatch;
            var depth = graphics.DepthValue;

            var dx = x1 - x0;
            var dy = y1 - y0;
            var invHalfWidth = (width * 0.5f) / (float)Math.Sqrt(dx * dx + dy * dy);
            dx *= invHalfWidth;
            dy *= invHalfWidth;

            if (miter)
            {
                x0 -= dx;
                y0 -= dy;
                x1 += dx;
                y1 += dy;
            }

            var i0 = (short)(batch.vtxIdx / 2 + 0);
            var i1 = (short)(batch.vtxIdx / 2 + 1);
            var i2 = (short)(batch.vtxIdx / 2 + 2);
            var i3 = (short)(batch.vtxIdx / 2 + 3);
            var i4 = (short)(batch.vtxIdx / 2 + 4);
            var i5 = (short)(batch.vtxIdx / 2 + 5);

            batch.idxArray[batch.idxIdx++] = i0;
            batch.idxArray[batch.idxIdx++] = i1;
            batch.idxArray[batch.idxIdx++] = i2;
            batch.idxArray[batch.idxIdx++] = i1;
            batch.idxArray[batch.idxIdx++] = i3;
            batch.idxArray[batch.idxIdx++] = i2;
            batch.idxArray[batch.idxIdx++] = i2;
            batch.idxArray[batch.idxIdx++] = i3;
            batch.idxArray[batch.idxIdx++] = i4;
            batch.idxArray[batch.idxIdx++] = i2;
            batch.idxArray[batch.idxIdx++] = i5;
            batch.idxArray[batch.idxIdx++] = i4;

            batch.vtxArray[batch.vtxIdx++] = x0 - dy;
            batch.vtxArray[batch.vtxIdx++] = y0 + dx;
            batch.vtxArray[batch.vtxIdx++] = x1 - dy;
            batch.vtxArray[batch.vtxIdx++] = y1 + dx;
            batch.vtxArray[batch.vtxIdx++] = x0;
            batch.vtxArray[batch.vtxIdx++] = y0;
            batch.vtxArray[batch.vtxIdx++] = x1;
            batch.vtxArray[batch.vtxIdx++] = y1;
            batch.vtxArray[batch.vtxIdx++] = x1 + dy;
            batch.vtxArray[batch.vtxIdx++] = y1 - dx;
            batch.vtxArray[batch.vtxIdx++] = x0 + dy;
            batch.vtxArray[batch.vtxIdx++] = y0 - dx;

            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();

            batch.dstArray[batch.dstIdx++] = 0;
            batch.dstArray[batch.dstIdx++] = 0;
            batch.dstArray[batch.dstIdx++] = encodedWidth;
            batch.dstArray[batch.dstIdx++] = encodedWidth;
            batch.dstArray[batch.dstIdx++] = 0;
            batch.dstArray[batch.dstIdx++] = 0;

            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
        }

        public void DrawLine(float x0, float y0, float x1, float y1, Color color, int width = 1, bool smooth = false, bool dash = false)
        {
            width += lineWidthBias;

            xform.TransformPoint(ref x0, ref y0);
            xform.TransformPoint(ref x1, ref y1);

            if (smooth)
            { 
                DrawThickSmoothLineInternal(x0, y0, x1, y1, color, width, false);
            }
            else if (width > 1)
            {
                DrawThickLineInternal(x0, y0, x1, y1, color, width, false);
            }
            else
            {
                DrawLineInternal(x0, y0, x1, y1, color, dash);
            }
        }

        public void DrawLine(List<float> points, Color color, int width = 1, bool smooth = false, bool miter = false)
        {
            DrawLine(CollectionsMarshal.AsSpan(points), color, width, smooth, miter);
        }

        public void DrawLine(Span<float> points, Color color, int width = 1, bool smooth = false, bool miter = false)
        {
            Debug.Assert(width > 1.0f || !miter);

            if (points.Length == 0)
                return;

            width += lineWidthBias;
            smooth |= width > 1.0f;

            var x0 = points[0];
            var y0 = points[1];

            xform.TransformPoint(ref x0, ref y0);

            for (int i = 2; i < points.Length; i += 2)
            {
                var x1 = points[i + 0];
                var y1 = points[i + 1];
                
                xform.TransformPoint(ref x1, ref y1);

                if (smooth)
                {
                    DrawThickSmoothLineInternal(x0, y0, x1, y1, color, width, false);
                }
                else if (width > 1)
                {
                    DrawThickLineInternal(x0, y0, x1, y1, color, width, false);
                }
                else
                {
                    DrawLineInternal(x0, y0, x1, y1, color, false);
                }

                x0 = x1;
                y0 = y1;
            }
        }

        public void DrawGeometry(Span<float> points, Color color, int width = 1, bool smooth = false, bool close = true)
        {
            width += lineWidthBias;
            
            var miter = false; //width > 1 && !xform.HasScaling; // Miter doesnt work with scaling atm.

            var x0 = points[0];
            var y0 = points[1];
            xform.TransformPoint(ref x0, ref y0);

            var numVerts = points.Length / 2;
            var numLines = numVerts - (close ? 0 : 1);

            for (int i = 0; i < numLines; i++)
            {
                var x1 = points[((i + 1) % numVerts) * 2 + 0];
                var y1 = points[((i + 1) % numVerts) * 2 + 1];
                xform.TransformPoint(ref x1, ref y1);

                if (smooth)
                {
                    DrawThickSmoothLineInternal(x0, y0, x1, y1, color, width, miter);
                }
                else if (width > 1)
                {
                    DrawThickLineInternal(x0, y0, x1, y1, color, width, miter);
                }
                else
                {
                    DrawLineInternal(x0, y0, x1, y1, color, false);
                }

                x0 = x1;
                y0 = y1;
            }
        }

        public void DrawRectangle(Rectangle rect, Color color, int width = 1, bool smooth = false)
        {
            DrawRectangle(rect.Left, rect.Top, rect.Right, rect.Bottom, color, width, smooth);
        }

        public void DrawRectangle(RectangleF rect, Color color, int width = 1, bool smooth = false)
        {
            DrawRectangle(rect.Left, rect.Top, rect.Right, rect.Bottom, color, width, smooth);
        }

        public void DrawRectangle(float x0, float y0, float x1, float y1, Color color, int width = 1, bool smooth = false)
        {
            width += lineWidthBias;

            xform.TransformPoint(ref x0, ref y0);
            xform.TransformPoint(ref x1, ref y1);

            var halfWidth = 0.0f; // Do we need miter on smooth rects?

            if (smooth)
            {
                DrawThickSmoothLineInternal(x0 - halfWidth, y0, x1 + halfWidth, y0, color, width, false);
                DrawThickSmoothLineInternal(x1, y0 - halfWidth, x1, y1 + halfWidth, color, width, false);
                DrawThickSmoothLineInternal(x0 - halfWidth, y1, x1 + halfWidth, y1, color, width, false);
                DrawThickSmoothLineInternal(x0, y0 - halfWidth, x0, y1 + halfWidth, color, width, false);
            }
            else if (width > 1)
            {
                halfWidth = width * 0.5f;
                DrawThickLineInternal(x0 - halfWidth, y0, x1 + halfWidth, y0, color, width, false);
                DrawThickLineInternal(x1, y0 - halfWidth, x1, y1 + halfWidth, color, width, false);
                DrawThickLineInternal(x0 - halfWidth, y1, x1 + halfWidth, y1, color, width, false);
                DrawThickLineInternal(x0, y0 - halfWidth, x0, y1 + halfWidth, color, width, false);
            }
            else
            {
                // Line rasterization rules makes is so that the last pixel is missing. So +1.
                DrawLineInternal(x0, y0, x1 + 1, y0, color, false);
                DrawLineInternal(x1, y0, x1, y1 + 1, color, false);
                DrawLineInternal(x0, y1, x1 + 1, y1, color, false);
                DrawLineInternal(x0, y0, x0, y1 + 1, color, false);
            }
        }
        public void FillGeometry(Span<float> geo, Color color, bool smooth = false)
        {
            FillGeometryInternal(geo, color, color, 0, smooth);
        }

        public void FillGeometryGradient(Span<float> geo, Color color0, Color color1, int gradientSize, bool smooth = false)
        {
            FillGeometryInternal(geo, color0, color1, gradientSize, smooth);
        }
        
        public void FillAndDrawGeometry(Span<float> geo, Color fillColor, Color lineColor, int lineWidth = 1, bool smooth = false)
        {
        	FillGeometry(geo, fillColor, smooth);
			DrawGeometry(geo, lineColor, lineWidth, smooth, true);
        }
		
        public void FillRectangle(float x0, float y0, float x1, float y1, Color color)
        {
            var batch = GetPolygonBatch();
            var depth = graphics.DepthValue;

            xform.TransformPoint(ref x0, ref y0);
            xform.TransformPoint(ref x1, ref y1);

            var i0 = (short)(batch.vtxIdx / 2 + 0);
            var i1 = (short)(batch.vtxIdx / 2 + 1);
            var i2 = (short)(batch.vtxIdx / 2 + 2);
            var i3 = (short)(batch.vtxIdx / 2 + 3);

            batch.idxArray[batch.idxIdx++] = i0;
            batch.idxArray[batch.idxIdx++] = i1;
            batch.idxArray[batch.idxIdx++] = i2;
            batch.idxArray[batch.idxIdx++] = i0;
            batch.idxArray[batch.idxIdx++] = i2;
            batch.idxArray[batch.idxIdx++] = i3;

            batch.vtxArray[batch.vtxIdx++] = x0;
            batch.vtxArray[batch.vtxIdx++] = y0;
            batch.vtxArray[batch.vtxIdx++] = x1;
            batch.vtxArray[batch.vtxIdx++] = y0;
            batch.vtxArray[batch.vtxIdx++] = x1;
            batch.vtxArray[batch.vtxIdx++] = y1;
            batch.vtxArray[batch.vtxIdx++] = x0;
            batch.vtxArray[batch.vtxIdx++] = y1;

            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();
            batch.colArray[batch.colIdx++] = color.ToAbgr();

            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;
            batch.depArray[batch.depIdx++] = depth;

            Debug.Assert(batch.colIdx * 2 == batch.vtxIdx);
        }

        public void FillRectangleGradient(float x0, float y0, float x1, float y1, Color color0, Color color1, bool vertical, float gradientSize)
        {
            var batch = GetPolygonBatch();
            var depth = graphics.DepthValue;

            xform.TransformPoint(ref x0, ref y0);
            xform.TransformPoint(ref x1, ref y1);
                
            bool fullHorizontalGradient = !vertical && Math.Abs(gradientSize) >= Math.Abs(x1 - x0);
            bool fullVerticalGradient   =  vertical && Math.Abs(gradientSize) >= Math.Abs(y1 - y0);

            if (fullHorizontalGradient || fullVerticalGradient)
            {
                var i0 = (short)(batch.vtxIdx / 2 + 0);
                var i1 = (short)(batch.vtxIdx / 2 + 1);
                var i2 = (short)(batch.vtxIdx / 2 + 2);
                var i3 = (short)(batch.vtxIdx / 2 + 3);

                batch.idxArray[batch.idxIdx++] = i0;
                batch.idxArray[batch.idxIdx++] = i1;
                batch.idxArray[batch.idxIdx++] = i2;
                batch.idxArray[batch.idxIdx++] = i0;
                batch.idxArray[batch.idxIdx++] = i2;
                batch.idxArray[batch.idxIdx++] = i3;

                batch.vtxArray[batch.vtxIdx++] = x0;
                batch.vtxArray[batch.vtxIdx++] = y0;
                batch.vtxArray[batch.vtxIdx++] = x1;
                batch.vtxArray[batch.vtxIdx++] = y0;
                batch.vtxArray[batch.vtxIdx++] = x1;
                batch.vtxArray[batch.vtxIdx++] = y1;
                batch.vtxArray[batch.vtxIdx++] = x0;
                batch.vtxArray[batch.vtxIdx++] = y1;

                if (fullHorizontalGradient)
                {
                    batch.colArray[batch.colIdx++] = color0.ToAbgr();
                    batch.colArray[batch.colIdx++] = color1.ToAbgr();
                    batch.colArray[batch.colIdx++] = color1.ToAbgr();
                    batch.colArray[batch.colIdx++] = color0.ToAbgr();
                }
                else
                {
                    batch.colArray[batch.colIdx++] = color0.ToAbgr();
                    batch.colArray[batch.colIdx++] = color0.ToAbgr();
                    batch.colArray[batch.colIdx++] = color1.ToAbgr();
                    batch.colArray[batch.colIdx++] = color1.ToAbgr();
                }

                batch.depArray[batch.depIdx++] = depth;
                batch.depArray[batch.depIdx++] = depth;
                batch.depArray[batch.depIdx++] = depth;
                batch.depArray[batch.depIdx++] = depth;
            }
            else
            {
                var i0 = (short)(batch.vtxIdx / 2 + 0);
                var i1 = (short)(batch.vtxIdx / 2 + 1);
                var i2 = (short)(batch.vtxIdx / 2 + 2);
                var i3 = (short)(batch.vtxIdx / 2 + 3);
                var i4 = (short)(batch.vtxIdx / 2 + 4);
                var i5 = (short)(batch.vtxIdx / 2 + 5);
                var i6 = (short)(batch.vtxIdx / 2 + 6);
                var i7 = (short)(batch.vtxIdx / 2 + 7);

                batch.idxArray[batch.idxIdx++] = i0;
                batch.idxArray[batch.idxIdx++] = i1;
                batch.idxArray[batch.idxIdx++] = i2;
                batch.idxArray[batch.idxIdx++] = i0;
                batch.idxArray[batch.idxIdx++] = i2;
                batch.idxArray[batch.idxIdx++] = i3;
                batch.idxArray[batch.idxIdx++] = i4;
                batch.idxArray[batch.idxIdx++] = i5;
                batch.idxArray[batch.idxIdx++] = i6;
                batch.idxArray[batch.idxIdx++] = i4;
                batch.idxArray[batch.idxIdx++] = i6;
                batch.idxArray[batch.idxIdx++] = i7;

                if (!vertical)
                {
                    float xm = x0 + gradientSize;

                    batch.vtxArray[batch.vtxIdx++] = x0;
                    batch.vtxArray[batch.vtxIdx++] = y0;
                    batch.vtxArray[batch.vtxIdx++] = xm;
                    batch.vtxArray[batch.vtxIdx++] = y0;
                    batch.vtxArray[batch.vtxIdx++] = xm;
                    batch.vtxArray[batch.vtxIdx++] = y1;
                    batch.vtxArray[batch.vtxIdx++] = x0;
                    batch.vtxArray[batch.vtxIdx++] = y1;
                    batch.vtxArray[batch.vtxIdx++] = xm;
                    batch.vtxArray[batch.vtxIdx++] = y0;
                    batch.vtxArray[batch.vtxIdx++] = x1;
                    batch.vtxArray[batch.vtxIdx++] = y0;
                    batch.vtxArray[batch.vtxIdx++] = x1;
                    batch.vtxArray[batch.vtxIdx++] = y1;
                    batch.vtxArray[batch.vtxIdx++] = xm;
                    batch.vtxArray[batch.vtxIdx++] = y1;
                }
                else
                {
                    float ym = y0 + gradientSize;

                    batch.vtxArray[batch.vtxIdx++] = x0;
                    batch.vtxArray[batch.vtxIdx++] = y0;
                    batch.vtxArray[batch.vtxIdx++] = x0;
                    batch.vtxArray[batch.vtxIdx++] = ym;
                    batch.vtxArray[batch.vtxIdx++] = x1;
                    batch.vtxArray[batch.vtxIdx++] = ym;
                    batch.vtxArray[batch.vtxIdx++] = x1;
                    batch.vtxArray[batch.vtxIdx++] = y0;
                    batch.vtxArray[batch.vtxIdx++] = x0;
                    batch.vtxArray[batch.vtxIdx++] = ym;
                    batch.vtxArray[batch.vtxIdx++] = x0;
                    batch.vtxArray[batch.vtxIdx++] = y1;
                    batch.vtxArray[batch.vtxIdx++] = x1;
                    batch.vtxArray[batch.vtxIdx++] = y1;
                    batch.vtxArray[batch.vtxIdx++] = x1;
                    batch.vtxArray[batch.vtxIdx++] = ym;
                }

                batch.colArray[batch.colIdx++] = color0.ToAbgr();
                batch.colArray[batch.colIdx++] = color1.ToAbgr();
                batch.colArray[batch.colIdx++] = color1.ToAbgr();
                batch.colArray[batch.colIdx++] = color0.ToAbgr();
                batch.colArray[batch.colIdx++] = color1.ToAbgr();
                batch.colArray[batch.colIdx++] = color1.ToAbgr();
                batch.colArray[batch.colIdx++] = color1.ToAbgr();
                batch.colArray[batch.colIdx++] = color1.ToAbgr();

                batch.depArray[batch.depIdx++] = depth;
                batch.depArray[batch.depIdx++] = depth;
                batch.depArray[batch.depIdx++] = depth;
                batch.depArray[batch.depIdx++] = depth;
                batch.depArray[batch.depIdx++] = depth;
                batch.depArray[batch.depIdx++] = depth;
                batch.depArray[batch.depIdx++] = depth;
                batch.depArray[batch.depIdx++] = depth;
            }

            Debug.Assert(batch.colIdx * 2 == batch.vtxIdx);
        }

        public void FillClipRegion(Color color)
        {
            Debug.Assert(!xform.HasScaling);

            var clipRect = graphics.CurrentClipRegion;
            var x = clipRect.X;
            var y = clipRect.Y;
            xform.ReverseTransformPoint(ref x, ref y);

            FillRectangle(x, y, x + clipRect.Width, y + clipRect.Height, color);
        }

        public void FillRectangle(Rectangle rect, Color color)
        {
            FillRectangle(rect.Left, rect.Top, rect.Right, rect.Bottom, color);
        }

        public void FillRectangleGradient(Rectangle rect, Color color0, Color color1, bool vertical, int gradientSize)
        {
            FillRectangleGradient(rect.Left, rect.Top, rect.Right, rect.Bottom, color0, color1, vertical, gradientSize);
        }

        public void FillRectangle(RectangleF rect, Color color)
        {
            FillRectangle(rect.Left, rect.Top, rect.Right, rect.Bottom, color);
        }

        public void FillAndDrawRectangle(float x0, float y0, float x1, float y1, Color fillColor, Color lineColor, int width = 1, bool smooth = false)
        {
            FillRectangle(x0, y0, x1, y1, fillColor);
            DrawRectangle(x0, y0, x1, y1, lineColor, width, smooth);
        }

        public void FillAndDrawRectangleGradient(float x0, float y0, float x1, float y1, Color fillColor0, Color fillColor1, Color lineColor, bool vertical, int gradientSize, int width = 1, bool smooth = false)
        {
            FillRectangleGradient(x0, y0, x1, y1, fillColor0, fillColor1, vertical, gradientSize);
            DrawRectangle(x0, y0, x1, y1, lineColor, width, smooth);
        }

        public void FillAndDrawRectangle(Rectangle rect, Color fillColor, Color lineColor, int width = 1, bool smooth = false)
        {
            FillRectangle(rect, fillColor);
            DrawRectangle(rect, lineColor, width, smooth);
        }

        public void FillAndDrawRectangleGradient(Rectangle rect, Color fillColor0, Color fillColor1, Color lineColor, bool vertical, int gradientSize, int width = 1, bool smooth = false)
        {
            FillRectangleGradient(rect, fillColor0, fillColor1, vertical, gradientSize);
            DrawRectangle(rect, lineColor, width, smooth);
        }

        private void FillGeometryInternal(Span<float> points, Color color0, Color color1, int gradientSize, bool smooth = false)
        {
            var gradient = gradientSize > 0;
            var batch = GetPolygonBatch();
            var i0 = (short)(batch.vtxIdx / 2);
            var numVerts = points.Length / 2;
            var depth = graphics.DepthValue;

            if (smooth)
            { 
                var px = points[points.Length - 2];
                var py = points[points.Length - 1];
                xform.TransformPoint(ref px, ref py);

                float cx = points[0];
                float cy = points[1];
                xform.TransformPoint(ref cx, ref cy);

                var dpx = cx - px;
                var dpy = cy - py;
                Utils.Normalize(ref dpx, ref dpy);

                for (int i = 0; i < numVerts; i++)
                {
                    var ni = (i + 1) % numVerts;

                    float nx = points[ni * 2 + 0];
                    float ny = points[ni * 2 + 1];

                    Color gradientColor;

                    if (gradient)
                    {
                        float lerp = ny / gradientSize;
                        byte r = (byte)(color0.R * (1.0f - lerp) + (color1.R * lerp));
                        byte g = (byte)(color0.G * (1.0f - lerp) + (color1.G * lerp));
                        byte b = (byte)(color0.B * (1.0f - lerp) + (color1.B * lerp));
                        byte a = (byte)(color0.A * (1.0f - lerp) + (color1.A * lerp));
                        gradientColor = new Color(r, g, b, a);
                    }
                    else
                    {
                        gradientColor = color0;
                    }

                    xform.TransformPoint(ref nx, ref ny);

                    var dnx = nx - cx;
                    var dny = ny - cy;
                    Utils.Normalize(ref dnx, ref dny);

                    var dx = (dnx - dpx) * 0.5f;
                    var dy = (dny - dpy) * 0.5f;
                    Utils.Normalize(ref dx, ref dy);

                    // Cos -> Csc
                    var d = 0.7071f / (float)Math.Sqrt(1.0f - Utils.Saturate(Utils.Dot(dnx, dny, -dpx, -dpy)));
                    var ix = cx + dx * d;
                    var iy = cy + dy * d;
                    var ox = cx - dx * d;
                    var oy = cy - dy * d;

                    batch.vtxArray[batch.vtxIdx++] = ix;
                    batch.vtxArray[batch.vtxIdx++] = iy;
                    batch.vtxArray[batch.vtxIdx++] = ox;
                    batch.vtxArray[batch.vtxIdx++] = oy;

                    batch.colArray[batch.colIdx++] = gradientColor.ToAbgr();
                    batch.colArray[batch.colIdx++] = Color.FromArgb(0, gradientColor).ToAbgr();

                    batch.depArray[batch.depIdx++] = depth;
                    batch.depArray[batch.depIdx++] = depth;

                    cx = nx;
                    cy = ny;
                    dpx = dnx;
                    dpy = dny;
                }

                // Simple fan for the inside
                for (int i = 0; i < numVerts - 2; i++)
                {
                    batch.idxArray[batch.idxIdx++] = i0;
                    batch.idxArray[batch.idxIdx++] = (short)(i0 + i * 2 + 2);
                    batch.idxArray[batch.idxIdx++] = (short)(i0 + i * 2 + 4);
                }

                // A few more quads for the anti-aliased section.
                for (int i = 0; i < numVerts; i++)
                {
                    var ni = (i + 1) % numVerts;

                    var qi0 = (short)(i0 + i  * 2 + 0);
                    var qi1 = (short)(i0 + i  * 2 + 1);
                    var qi2 = (short)(i0 + ni * 2 + 0);
                    var qi3 = (short)(i0 + ni * 2 + 1);

                    batch.idxArray[batch.idxIdx++] = qi0;
                    batch.idxArray[batch.idxIdx++] = qi1;
                    batch.idxArray[batch.idxIdx++] = qi2;
                    batch.idxArray[batch.idxIdx++] = qi1;
                    batch.idxArray[batch.idxIdx++] = qi3;
                    batch.idxArray[batch.idxIdx++] = qi2;
                }
            }
            else
            {
                for (int i = 0; i < numVerts; i++)
                {
                    var ni = (i + 1) % numVerts;

                    float nx = points[ni * 2 + 0];
                    float ny = points[ni * 2 + 1];

                    Color gradientColor;

                    if (gradient)
                    {
                        float lerp = ny / gradientSize;
                        byte r = (byte)(color0.R * (1.0f - lerp) + (color1.R * lerp));
                        byte g = (byte)(color0.G * (1.0f - lerp) + (color1.G * lerp));
                        byte b = (byte)(color0.B * (1.0f - lerp) + (color1.B * lerp));
                        byte a = (byte)(color0.A * (1.0f - lerp) + (color1.A * lerp));
                        gradientColor = new Color(r, g, b, a);
                    }
                    else
                    {
                        gradientColor = color0;
                    }

                    xform.TransformPoint(ref nx, ref ny);

                    batch.vtxArray[batch.vtxIdx++] = nx;
                    batch.vtxArray[batch.vtxIdx++] = ny;
                    batch.colArray[batch.colIdx++] = gradientColor.ToAbgr();
                    batch.depArray[batch.depIdx++] = depth;
                }

                // Simple fan
                for (int i = 0; i < numVerts - 2; i++)
                {
                    batch.idxArray[batch.idxIdx++] = i0;
                    batch.idxArray[batch.idxIdx++] = (short)(i0 + i + 1);
                    batch.idxArray[batch.idxIdx++] = (short)(i0 + i + 2);
                }
            }
        }

        public void DrawText(string text, Font font, float x, float y, Color color, TextFlags flags = TextFlags.None, float width = 0, float height = 0, float clipMinX = 0, float clipMaxX = 0)
        {
            if (string.IsNullOrEmpty(text))
                return;

            Debug.Assert(!flags.HasFlag(TextFlags.Clip) || !flags.HasFlag(TextFlags.Ellipsis));
            Debug.Assert(!flags.HasFlag(TextFlags.Monospace) || !flags.HasFlag(TextFlags.Ellipsis));
            Debug.Assert(!flags.HasFlag(TextFlags.Monospace) || !flags.HasFlag(TextFlags.Clip));
            Debug.Assert(!flags.HasFlag(TextFlags.Ellipsis) || width > 0);
            Debug.Assert((flags & TextFlags.HorizontalAlignMask) != TextFlags.Center || width  > 0);
            Debug.Assert((flags & TextFlags.VerticalAlignMask)   == TextFlags.Top    || height > 0);

            if (!texts.TryGetValue(font, out var list))
            {
                list = new List<TextInstance>();
                texts.Add(font, list);
            }

            xform.TransformPoint(ref x, ref y);

            var inst = new TextInstance();
            inst.layoutRect = new RectangleF(x, y, width, height);
            inst.flags = flags;
            inst.text = text;
            inst.color = color;
            inst.depth = graphics.DepthValue;

            if (clipMaxX > clipMinX)
            {
                var dummy = 0.0f;
                xform.TransformPoint(ref clipMinX, ref dummy);
                xform.TransformPoint(ref clipMaxX, ref dummy);
                inst.clipRect = new RectangleF(clipMinX, y, clipMaxX - clipMinX, height); 
            }
            else
            {
                inst.clipRect =  inst.layoutRect;
            }

            list.Add(inst);
        }

        public void DrawBitmap(Bitmap bmp, float x, float y, float opacity = 1.0f, Color tint = new Color())
        {
            Debug.Assert(Utils.Frac(x) == 0.0f && Utils.Frac(y) == 0.0f);
            DrawBitmap(bmp, x, y, bmp.Size.Width, bmp.Size.Height, opacity, 0, 0, 1, 1, false, tint);
        }

        public void DrawBitmapScaled(Bitmap bmp, float x, float y, float sx, float sy)
        {
            DrawBitmap(bmp, x, y, sx, sy, 1, 0, 0, 1, 1);
        }

        public void DrawBitmapCentered(Bitmap bmp, float x, float y, float width, float height, float opacity = 1.0f, Color tint = new Color())
        {
            x += (width  - bmp.Size.Width)  / 2;
            y += (height - bmp.Size.Height) / 2;
            DrawBitmap(bmp, x, y, opacity, tint);
        }

        public void DrawBitmapAtlas(BitmapAtlasRef bmp, float x, float y, float opacity = 1.0f, float scale = 1.0f, Color tint = new Color())
        {
            Debug.Assert(Utils.Frac(x) == 0.0f && Utils.Frac(y) == 0.0f);
            var atlas = bmp.Atlas;
            var elementIndex = bmp.ElementIndex;
            var elementSize = bmp.ElementSize;
            atlas.GetElementUVs(elementIndex, out var u0, out var v0, out var u1, out var v1);
            DrawBitmap(atlas, x, y, elementSize.Width * scale, elementSize.Height * scale, opacity, u0, v0, u1, v1, false, tint);
        }

        public void DrawBitmapAtlasCentered(BitmapAtlasRef bmp, float x, float y, float width, float height, float opacity = 1.0f, float scale = 1.0f, Color tint = new Color())
        {
            x += (width  - bmp.ElementSize.Width)  / 2;
            y += (height - bmp.ElementSize.Height) / 2;
            DrawBitmapAtlas(bmp, x, y, opacity, scale, tint);
        }

        public void DrawBitmapAtlasCentered(BitmapAtlasRef bmp, Rectangle rect, float opacity = 1.0f, float scale = 1.0f, Color tint = new Color())
        {
            float x = rect.Left + (rect.Width  - bmp.ElementSize.Width)  / 2;
            float y = rect.Top  + (rect.Height - bmp.ElementSize.Height) / 2;
            DrawBitmapAtlas(bmp, x, y, opacity, scale, tint);
        }

        public void DrawBitmap(Bitmap bmp, float x, float y, float width, float height, float opacity, float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 1, bool rotated = false, Color tint = new Color())
        {
            Debug.Assert(Utils.Frac(x) == 0.0f && Utils.Frac(y) == 0.0f);
            if (!bitmaps.TryGetValue(bmp, out var list))
            {
                list = new List<BitmapInstance>();
                bitmaps.Add(bmp, list);
            }

            xform.TransformPoint(ref x, ref y);
            xform.ScaleSize(ref width, ref height);

            var inst = new BitmapInstance();
            inst.x = x;
            inst.y = y;
            inst.sx = width;
            inst.sy = height;
            inst.tint = tint;
            inst.depth = graphics.DepthValue;

            if (bmp.IsAtlas && bmp.Filtering) 
            {
                // Prevent leaking from other images in the atlas.
                var halfPixelX = 0.5f / bmp.Size.Width;
                var halfPixelY = 0.5f / bmp.Size.Height;

                inst.u0 = u0 + halfPixelX;
                inst.v0 = v0 + halfPixelY;
                inst.u1 = u1 - halfPixelX;
                inst.v1 = v1 - halfPixelY;
            }
            else
            {
                inst.u0 = u0;
                inst.v0 = v0;
                inst.u1 = u1;
                inst.v1 = v1;
            }

            inst.opacity = opacity;
            inst.rotated = rotated;

            list.Add(inst);
        }

        public PolyDrawData GetPolygonDrawData()
        {
            var draw = (PolyDrawData)null;

            if (polyBatch != null)
            {
                draw = new PolyDrawData();
                draw.vtxArray = polyBatch.vtxArray;
                draw.colArray = polyBatch.colArray;
                draw.idxArray = polyBatch.idxArray;
                draw.depArray = polyBatch.depArray;
                draw.numIndices = polyBatch.idxIdx;
                draw.vtxArraySize = polyBatch.vtxIdx;
                draw.colArraySize = polyBatch.colIdx;
                draw.idxArraySize = polyBatch.idxIdx;
                draw.depArraySize = polyBatch.depIdx;
            }

            return draw;
        }

        public LineSmoothDrawData GetSmoothLineDrawData()
        {
            var draw = (LineSmoothDrawData)null;

            if (lineSmoothBatch != null)
            { 
                draw = new LineSmoothDrawData();
                draw.vtxArray = lineSmoothBatch.vtxArray;
                draw.dstArray = lineSmoothBatch.dstArray;
                draw.colArray = lineSmoothBatch.colArray;
                draw.idxArray = lineSmoothBatch.idxArray;
                draw.depArray = lineSmoothBatch.depArray;
                draw.numIndices = lineSmoothBatch.idxIdx;
                draw.vtxArraySize = lineSmoothBatch.vtxIdx;
                draw.dstArraySize = lineSmoothBatch.dstIdx;
                draw.colArraySize = lineSmoothBatch.colIdx;
                draw.idxArraySize = lineSmoothBatch.idxIdx;
                draw.depArraySize = lineSmoothBatch.depIdx;
            }

            return draw;
        }

        public LineDrawData GetLineDrawData()
        {
            var draw = (LineDrawData)null;

            if (lineBatch != null)
            {
                draw = new LineDrawData();
                draw.vtxArray = lineBatch.vtxArray;
                draw.texArray = lineBatch.texArray;
                draw.colArray = lineBatch.colArray;
                draw.depArray = lineBatch.depArray;
                draw.numVertices = lineBatch.vtxIdx / 2;
                draw.vtxArraySize = lineBatch.vtxIdx;
                draw.texArraySize = lineBatch.texIdx;
                draw.colArraySize = lineBatch.colIdx;
                draw.depArraySize = lineBatch.depIdx;
            }

            return draw;
        }

        // MATTT : Create an object that contains everything.
        public List<DrawData> GetTextDrawData(float[] vtxArray, float[] texArray, int[] colArray, byte[] depArray, out int vtxArraySize, out int texArraySize, out int colArraySize, out int depArraySize, out int idxArraySize)
        {
            var drawData = new List<DrawData>();

            var vtxIdx = 0;
            var texIdx = 0;
            var colIdx = 0;
            var depIdx = 0;
            var idxIdx = 0;

            foreach (var kv in texts)
            {
                var font = kv.Key;
                var list = kv.Value;
                var draw = new DrawData();

                draw.textureId = font.Texture;
                draw.start = idxIdx;

                foreach (var inst in list)
                {
                    var alignmentOffsetX = 0;
                    var alignmentOffsetY = font.OffsetY;
                    var mono = inst.flags.HasFlag(TextFlags.Monospace);

                    if (inst.flags.HasFlag(TextFlags.Ellipsis))
                    {
                        var ellipsisSizeX = font.MeasureString("...", mono) * 2; // Leave some padding.
                        if (font.TruncateString(ref inst.text, (int)(inst.layoutRect.Width - ellipsisSizeX)))
                            inst.text += "...";
                    }

                    var halign = inst.flags & TextFlags.HorizontalAlignMask;
                    var valign = inst.flags & TextFlags.VerticalAlignMask;

                    if (halign != TextFlags.Left)
                    {
                        var minX = 0;
                        var maxX = font.MeasureString(inst.text, mono);

                        if (halign == TextFlags.Center)
                        {
                            alignmentOffsetX -= minX;
                            alignmentOffsetX += ((int)inst.layoutRect.Width - maxX - minX) / 2;
                        }
                        else if (halign == TextFlags.Right)
                        {
                            alignmentOffsetX -= minX;
                            alignmentOffsetX += ((int)inst.layoutRect.Width - maxX - minX);
                        }
                    }

                    if (valign != TextFlags.Top)
                    {
                        // Use a tall character with no descender as reference.
                        var charA = font.GetCharInfo('A');

                        // When aligning middle or center, ignore the y offset since it just
                        // adds extra padding and messes up calculations.
                        alignmentOffsetY = -charA.yoffset;

                        if (valign == TextFlags.Middle)
                        {
                            alignmentOffsetY += ((int)inst.layoutRect.Height - charA.height + 1) / 2;
                        }
                        else if (valign == TextFlags.Bottom)
                        {
                            alignmentOffsetY += ((int)inst.layoutRect.Height - charA.height);
                        }
                    }

                    var packedColor = inst.color.ToAbgr();
                    var numVertices = inst.text.Length * 4;

                    int x = (int)(inst.layoutRect.X + alignmentOffsetX);
                    int y = (int)(inst.layoutRect.Y + alignmentOffsetY);
                    
                    if (mono)
                    {
                        var infoMono = font.GetCharInfo('0');

                        for (int i = 0; i < inst.text.Length; i++)
                        {
                            var c0 = inst.text[i];
                            var info = font.GetCharInfo(c0);

                            var monoAjustX = (infoMono.width  - info.width  + 1) / 2;
                            var monoAjustY = (infoMono.height - info.height + 1) / 2;

                            var x0 = x + info.xoffset + monoAjustX;
                            var y0 = y + info.yoffset;
                            var x1 = x0 + info.width;
                            var y1 = y0 + info.height;

                            vtxArray[vtxIdx++] = x0;
                            vtxArray[vtxIdx++] = y0;
                            vtxArray[vtxIdx++] = x1;
                            vtxArray[vtxIdx++] = y0;
                            vtxArray[vtxIdx++] = x1;
                            vtxArray[vtxIdx++] = y1;
                            vtxArray[vtxIdx++] = x0;
                            vtxArray[vtxIdx++] = y1;

                            texArray[texIdx++] = info.u0;
                            texArray[texIdx++] = info.v0;
                            texArray[texIdx++] = info.u1;
                            texArray[texIdx++] = info.v0;
                            texArray[texIdx++] = info.u1;
                            texArray[texIdx++] = info.v1;
                            texArray[texIdx++] = info.u0;
                            texArray[texIdx++] = info.v1;

                            colArray[colIdx++] = packedColor;
                            colArray[colIdx++] = packedColor;
                            colArray[colIdx++] = packedColor;
                            colArray[colIdx++] = packedColor;

                            depArray[depIdx++] = inst.depth;
                            depArray[depIdx++] = inst.depth;
                            depArray[depIdx++] = inst.depth;
                            depArray[depIdx++] = inst.depth;

                            x += infoMono.xadvance;
                        }

                        idxIdx += inst.text.Length * 6;
                        draw.count += inst.text.Length * 6;
                    }
                    else if (inst.flags.HasFlag(TextFlags.Clip)) // Slow path when there is clipping.
                    {
                        var clipMinX = (int)(inst.clipRect.X);
                        var clipMaxX = (int)(inst.clipRect.X + inst.clipRect.Width);

                        for (int i = 0; i < inst.text.Length; i++)
                        {
                            var c0 = inst.text[i];
                            var info = font.GetCharInfo(c0);

                            var x0 = x + info.xoffset;
                            var y0 = y + info.yoffset;
                            var x1 = x0 + info.width;
                            var y1 = y0 + info.height;

                            if (x1 > clipMinX && x0 < clipMaxX)
                            {
                                var u0 = info.u0;
                                var v0 = info.v0;
                                var u1 = info.u1;
                                var v1 = info.v1;

                                var newu0 = u0;
                                var newu1 = u1;
                                var newx0 = x0;
                                var newx1 = x1;

                                // Left clipping.
                                if (x0 < clipMinX && x1 > clipMinX)
                                {
                                    newu0 = Utils.Lerp(info.u0, info.u1, ((clipMinX - x0) / (float)(x1 - x0)));
                                    newx0 = clipMinX;
                                }

                                // Right clipping
                                if (x0 < clipMaxX && x1 > clipMaxX)
                                {
                                    newu1 = Utils.Lerp(info.u0, info.u1, ((clipMaxX - x0) / (float)(x1 - x0)));
                                    newx1 = clipMaxX;
                                }

                                u0 = newu0;
                                u1 = newu1;
                                x0 = newx0;
                                x1 = newx1;

                                vtxArray[vtxIdx++] = x0;
                                vtxArray[vtxIdx++] = y0;
                                vtxArray[vtxIdx++] = x1;
                                vtxArray[vtxIdx++] = y0;
                                vtxArray[vtxIdx++] = x1;
                                vtxArray[vtxIdx++] = y1;
                                vtxArray[vtxIdx++] = x0;
                                vtxArray[vtxIdx++] = y1;

                                texArray[texIdx++] = u0;
                                texArray[texIdx++] = v0;
                                texArray[texIdx++] = u1;
                                texArray[texIdx++] = v0;
                                texArray[texIdx++] = u1;
                                texArray[texIdx++] = v1;
                                texArray[texIdx++] = u0;
                                texArray[texIdx++] = v1;

                                colArray[colIdx++] = packedColor;
                                colArray[colIdx++] = packedColor;
                                colArray[colIdx++] = packedColor;
                                colArray[colIdx++] = packedColor;

                                depArray[depIdx++] = inst.depth;
                                depArray[depIdx++] = inst.depth;
                                depArray[depIdx++] = inst.depth;
                                depArray[depIdx++] = inst.depth;

                                idxIdx += 6;
                                draw.count += 6;
                            }

                            x += info.xadvance;
                            if (i != inst.text.Length - 1)
                            {
                                char c1 = inst.text[i + 1];
                                x += font.GetKerning(c0, c1);
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < inst.text.Length; i++)
                        {
                            var c0 = inst.text[i];
                            var info = font.GetCharInfo(c0);

                            var x0 = x + info.xoffset;
                            var y0 = y + info.yoffset;
                            var x1 = x0 + info.width;
                            var y1 = y0 + info.height;

                            vtxArray[vtxIdx++] = x0;
                            vtxArray[vtxIdx++] = y0;
                            vtxArray[vtxIdx++] = x1;
                            vtxArray[vtxIdx++] = y0;
                            vtxArray[vtxIdx++] = x1;
                            vtxArray[vtxIdx++] = y1;
                            vtxArray[vtxIdx++] = x0;
                            vtxArray[vtxIdx++] = y1;

                            texArray[texIdx++] = info.u0;
                            texArray[texIdx++] = info.v0;
                            texArray[texIdx++] = info.u1;
                            texArray[texIdx++] = info.v0;
                            texArray[texIdx++] = info.u1;
                            texArray[texIdx++] = info.v1;
                            texArray[texIdx++] = info.u0;
                            texArray[texIdx++] = info.v1;

                            colArray[colIdx++] = packedColor;
                            colArray[colIdx++] = packedColor;
                            colArray[colIdx++] = packedColor;
                            colArray[colIdx++] = packedColor;

                            depArray[depIdx++] = inst.depth;
                            depArray[depIdx++] = inst.depth;
                            depArray[depIdx++] = inst.depth;
                            depArray[depIdx++] = inst.depth;

                            x += info.xadvance;
                            if (i != inst.text.Length - 1)
                            {
                                char c1 = inst.text[i + 1];
                                x += font.GetKerning(c0, c1);
                            }
                        }

                        idxIdx += inst.text.Length * 6;
                        draw.count += inst.text.Length * 6;
                    }
                }

                drawData.Add(draw);
            }

            vtxArraySize = vtxIdx;
            texArraySize = texIdx;
            colArraySize = colIdx;
            depArraySize = depIdx;
            idxArraySize = idxIdx;

            return drawData;
        }

        public List<DrawData> GetBitmapDrawData(float[] vtxArray, float[] texArray, int[] colArray, byte[] depArray, out int vtxArraySize, out int texArraySize, out int colArraySize, out int depArraySize, out int idxArraySize)
        {
            var drawData = new List<DrawData>();

            var vtxIdx = 0;
            var texIdx = 0;
            var colIdx = 0;
            var depIdx = 0;
            var idxIdx = 0;

            foreach (var kv in bitmaps)
            {
                var bmp = kv.Key;
                var list = kv.Value;
                var draw = new DrawData();

                draw.textureId = bmp.Id;
                draw.start = idxIdx;
                
                foreach (var inst in list)
                {
                    var x0 = inst.x;
                    var y0 = inst.y;
                    var x1 = inst.x + inst.sx;
                    var y1 = inst.y + inst.sy;
                    var tint = inst.tint != Color.Empty ? inst.tint : Color.White;

                    vtxArray[vtxIdx++] = x0;
                    vtxArray[vtxIdx++] = y0;
                    vtxArray[vtxIdx++] = x1;
                    vtxArray[vtxIdx++] = y0;
                    vtxArray[vtxIdx++] = x1;
                    vtxArray[vtxIdx++] = y1;
                    vtxArray[vtxIdx++] = x0;
                    vtxArray[vtxIdx++] = y1;

                    if (inst.rotated)
                    {
                        texArray[texIdx++] = inst.u1;
                        texArray[texIdx++] = inst.v0;
                        texArray[texIdx++] = inst.u1;
                        texArray[texIdx++] = inst.v1;
                        texArray[texIdx++] = inst.u0;
                        texArray[texIdx++] = inst.v1;
                        texArray[texIdx++] = inst.u0;
                        texArray[texIdx++] = inst.v0;
                    }
                    else
                    {
                        texArray[texIdx++] = inst.u0;
                        texArray[texIdx++] = inst.v0;
                        texArray[texIdx++] = inst.u1;
                        texArray[texIdx++] = inst.v0;
                        texArray[texIdx++] = inst.u1;
                        texArray[texIdx++] = inst.v1;
                        texArray[texIdx++] = inst.u0;
                        texArray[texIdx++] = inst.v1;
                    }

                    var packedOpacity = new Color(tint.R, tint.G, tint.B, (int)(inst.opacity * 255)).ToAbgr();
                    colArray[colIdx++] = packedOpacity;
                    colArray[colIdx++] = packedOpacity;
                    colArray[colIdx++] = packedOpacity;
                    colArray[colIdx++] = packedOpacity;

                    depArray[depIdx++] = inst.depth;
                    depArray[depIdx++] = inst.depth;
                    depArray[depIdx++] = inst.depth;
                    depArray[depIdx++] = inst.depth;

                    draw.count += 6;
                    idxIdx += 6;
                }

                drawData.Add(draw);
            }

            vtxArraySize = vtxIdx;
            texArraySize = texIdx;
            colArraySize = colIdx;
            depArraySize = depIdx;
            idxArraySize = idxIdx;

            return drawData;
        }
    }
}
