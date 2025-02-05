﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Android.Opengl;
using Android.Runtime;
using Java.Nio;
using Javax.Microedition.Khronos.Opengles;

using Bitmap = Android.Graphics.Bitmap;

namespace FamiStudio
{
    public class Graphics : GraphicsBase
    {
        // Must be powers of two.
        const int MinBufferSize = 16;
        const int MaxBufferSize = 128 * 1024;

        const int MinBufferSizeLog2 = 4;
        const int MaxBufferSizeLog2 = 18;
        const int NumBufferSizes    = MaxBufferSizeLog2 - MinBufferSizeLog2 + 1;

        // Index [0] is MaxBufferSize
        // Index [1] is MaxBufferSize / 2
        // Index [2] is MaxBufferSize / 4
        // ...
        List<FloatBuffer>[] freeVtxBuffers = new List<FloatBuffer>[NumBufferSizes];
        List<IntBuffer>[]   freeColBuffers = new List<IntBuffer>  [NumBufferSizes];
        List<ShortBuffer>[] freeIdxBuffers = new List<ShortBuffer>[NumBufferSizes];

        List<FloatBuffer>[] usedVtxBuffers = new List<FloatBuffer>[NumBufferSizes];
        List<IntBuffer>[]   usedColBuffers = new List<IntBuffer>  [NumBufferSizes];
        List<ShortBuffer>[] usedIdxBuffers = new List<ShortBuffer>[NumBufferSizes];

        public Graphics(float mainScale, float baseScale) : base(mainScale, baseScale)
        {
            dashedBitmap = CreateBitmapFromResource("Dash");
            GLES11.GlTexParameterx(GLES11.GlTexture2d, GLES11.GlTextureWrapS, GLES11.GlRepeat);
            GLES11.GlTexParameterx(GLES11.GlTexture2d, GLES11.GlTextureWrapT, GLES11.GlRepeat);

            for (int i = 0; i < NumBufferSizes; i++)
            {
                freeVtxBuffers[i] = new List<FloatBuffer>();
                freeColBuffers[i] = new List<IntBuffer>();
                freeIdxBuffers[i] = new List<ShortBuffer>();
                usedVtxBuffers[i] = new List<FloatBuffer>();
                usedColBuffers[i] = new List<IntBuffer>();
                usedIdxBuffers[i] = new List<ShortBuffer>();
            }

            var smoothLineWidths = new int[2];
            GLES11.GlGetIntegerv(GLES11.GlSmoothLineWidthRange, smoothLineWidths, 0);
            maxSmoothLineWidth = smoothLineWidths[1];
        }

        public override void BeginDrawFrame()
        {
            base.BeginDrawFrame();

            for (int i = 0; i < NumBufferSizes; i++)
            {
                freeVtxBuffers[i].AddRange(usedVtxBuffers[i]);
                freeColBuffers[i].AddRange(usedColBuffers[i]);
                freeIdxBuffers[i].AddRange(usedIdxBuffers[i]);
                usedVtxBuffers[i].Clear();
                usedColBuffers[i].Clear();
                usedIdxBuffers[i].Clear();
            }
        }

        public override void BeginDrawControl(Rectangle unflippedControlRect, int windowSizeY)
        {
            base.BeginDrawControl(unflippedControlRect, windowSizeY);

            GLES11.GlHint(GLES11.GlLineSmoothHint, GLES11.GlNicest);
            GLES11.GlViewport(controlRectFlip.Left, controlRectFlip.Top, controlRectFlip.Width, controlRectFlip.Height);
            GLES11.GlMatrixMode(GLES11.GlProjection);
            GLES11.GlLoadIdentity();
            GLES11.GlOrthof(0, unflippedControlRect.Width, unflippedControlRect.Height, 0, -1, 1);
            GLES11.GlDisable((int)2884); // Cull face?
            GLES11.GlMatrixMode(GLES11.GlModelview);
            GLES11.GlLoadIdentity();
            GLES11.GlBlendFunc(GLES11.GlSrcAlpha, GLES11.GlOneMinusSrcAlpha);
            GLES11.GlEnable(GLES11.GlBlend);
            GLES11.GlDisable(GLES11.GlDepthTest);
            GLES11.GlDisable(GLES11.GlStencilTest);
            GLES11.GlEnable(GLES11.GlScissorTest);
            GLES11.GlScissor(controlRectFlip.Left, controlRectFlip.Top, controlRectFlip.Width, controlRectFlip.Height);
            GLES11.GlEnableClientState(GLES11.GlVertexArray);
        }

        private void SetScissorRect(int x0, int y0, int x1, int y1)
        {
            var scissor = new Rectangle(controlRect.X + x0, controlRect.Y + y0, x1 - x0, y1 - y0);
            scissor = FlipRectangleY(scissor);
            GLES11.GlScissor(scissor.Left, scissor.Top, scissor.Width, scissor.Height);
        }

        private void ClearScissorRect()
        {
            GLES11.GlScissor(controlRectFlip.Left, controlRectFlip.Top, controlRectFlip.Width, controlRectFlip.Height);
        }

        public void SetViewport(int x, int y, int width, int height)
        {
            GLES11.GlViewport(x, y, width, height);
        }

        public void Clear(Color color)
        {
            GLES11.GlClearColor(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
            GLES11.GlClear(GLES11.GlColorBufferBit);
        }

        public void UpdateBitmap(Bitmap bmp, int x, int y, int width, int height, int[] data)
        {
            var buffer = ByteBuffer.AllocateDirect(width * height * sizeof(int)).Order(ByteOrder.NativeOrder()).AsIntBuffer();
            buffer.Put(data);
            buffer.Position(0);

            GLES11.GlBindTexture(GLES11.GlTexture2d, bmp.Id);
            GLES11.GlTexSubImage2D(GLES11.GlTexture2d, 0, x, y, width, height, GLES11.GlRgba, GLES11.GlUnsignedByte, buffer);
        }

        protected override int CreateEmptyTexture(int width, int height, bool alpha, bool filter)
        {
            var id = new int[1];
            GLES11.GlGenTextures(1, id, 0);
            GLES11.GlBindTexture(GLES11.GlTexture2d, id[0]);
            GLES11.GlTexParameterx(GLES11.GlTexture2d, GLES11.GlTextureMinFilter, filter ? GLES11.GlLinear : GLES11.GlNearest);
            GLES11.GlTexParameterx(GLES11.GlTexture2d, GLES11.GlTextureMagFilter, filter ? GLES11.GlLinear : GLES11.GlNearest);
            GLES11.GlTexParameterx(GLES11.GlTexture2d, GLES11.GlTextureWrapS, GLES11.GlClampToEdge);
            GLES11.GlTexParameterx(GLES11.GlTexture2d, GLES11.GlTextureWrapT, GLES11.GlClampToEdge);

            var buffer = ByteBuffer.AllocateDirect(width * height * sizeof(int)).Order(ByteOrder.NativeOrder()).AsIntBuffer();
            buffer.Put(new int[width * height]);
            buffer.Position(0);

            GLES11.GlTexImage2D(GLES11.GlTexture2d, 0, GLES11.GlRgba, width, height, 0, GLES11.GlRgba, GLES11.GlUnsignedByte, buffer);

            return id[0];
        }

        protected override int CreateTexture(SimpleBitmap bmp, bool filter)
        {
            var buffer = IntBuffer.Wrap(bmp.Data);
            var id = new int[1];
            GLES11.GlGenTextures(1, id, 0);
            GLES11.GlBindTexture(GLES11.GlTexture2d, id[0]);
            GLES11.GlTexImage2D(GLES11.GlTexture2d, 0, GLES11.GlRgba, bmp.Width, bmp.Height, 0, GLES11.GlRgba, GLES11.GlUnsignedByte, buffer);
            GLES11.GlTexParameterx(GLES11.GlTexture2d, GLES11.GlTextureMinFilter, filter ? GLES11.GlLinear : GLES11.GlNearest);
            GLES11.GlTexParameterx(GLES11.GlTexture2d, GLES11.GlTextureMagFilter, filter ? GLES11.GlLinear : GLES11.GlNearest);
            GLES11.GlTexParameterx(GLES11.GlTexture2d, GLES11.GlTextureWrapS, GLES11.GlClampToEdge);
            GLES11.GlTexParameterx(GLES11.GlTexture2d, GLES11.GlTextureWrapT, GLES11.GlClampToEdge);
            
            return id[0];
        }

        public override void DeleteTexture(int id)
        {
            var ids = new[] { id };
            GLES11.GlDeleteTextures(1, ids, 0);
        }

        protected override string GetScaledFilename(string name, out bool needsScaling)
        {
            var assembly = Assembly.GetExecutingAssembly();
            needsScaling = false;

            if (windowScaling >= 4.0f && assembly.GetManifestResourceInfo($"FamiStudio.Resources.{name}@4x.tga") != null)
            {
                return $"FamiStudio.Resources.{name}@4x.tga";
            }
            else if (windowScaling >= 2.0f && assembly.GetManifestResourceInfo($"FamiStudio.Resources.{name}@2x.tga") != null)
            {
                return $"FamiStudio.Resources.{name}@2x.tga";
            }
            else
            {
                return $"FamiStudio.Resources.{name}.tga";
            }
        }

        public Bitmap CreateBitmapFromResource(string name)
        {
            var bmp = LoadBitmapFromResourceWithScaling(name);
            return new Bitmap(this, CreateTexture(bmp, true), bmp.Width, bmp.Height, true, true);
        }

        public override BitmapAtlas CreateBitmapAtlasFromResources(string[] names)
        {
            // Need to sort since we do binary searches on the names.
            Array.Sort(names);

            var bitmaps = new SimpleBitmap[names.Length];
            var elementSizeX = 0;
            var elementSizeY = 0;

            for (int i = 0; i < names.Length; i++)
            {
                var bmp = LoadBitmapFromResourceWithScaling(names[i]);

                elementSizeX = Math.Max(elementSizeX, bmp.Width);
                elementSizeY = Math.Max(elementSizeY, bmp.Height);

                bitmaps[i] = bmp;
            }

            Debug.Assert(elementSizeX < MaxAtlasResolution);

            var elementsPerRow = MaxAtlasResolution / elementSizeX;
            var elementRects = new Rectangle[names.Length];
            var atlasSizeX = 0;
            var atlasSizeY = 0;

            for (int i = 0; i < names.Length; i++)
            {
                var bmp = bitmaps[i];
                var row = i / elementsPerRow;
                var col = i % elementsPerRow;

                elementRects[i] = new Rectangle(
                    col * elementSizeX,
                    row * elementSizeY,
                    bmp.Width,
                    bmp.Height);

                atlasSizeX = Math.Max(atlasSizeX, elementRects[i].Right);
                atlasSizeY = Math.Max(atlasSizeY, elementRects[i].Bottom);
            }

            atlasSizeX = Utils.NextPowerOfTwo(atlasSizeX);
            atlasSizeY = Utils.NextPowerOfTwo(atlasSizeY);

            var textureId = CreateEmptyTexture(atlasSizeX, atlasSizeY, true, true);
            GLES11.GlBindTexture(GLES11.GlTexture2d, textureId);

            Debug.WriteLine($"Creating bitmap atlas of size {atlasSizeX}x{atlasSizeY} with {names.Length} images:");

            for (int i = 0; i < names.Length; i++)
            {
                var bmp = bitmaps[i];
                var buffer = IntBuffer.Wrap(bmp.Data);

                Debug.WriteLine($"  - {names[i]} ({bmp.Width} x {bmp.Height}):");

                GLES11.GlTexSubImage2D(GLES11.GlTexture2d, 0, elementRects[i].X, elementRects[i].Y, bmp.Width, bmp.Height, GLES11.GlRgba, GLES11.GlUnsignedByte, buffer);
            }

            return new BitmapAtlas(this, textureId, atlasSizeX, atlasSizeY, names, elementRects, true);
        }

        public Bitmap CreateBitmapFromOffscreenGraphics(OffscreenGraphics g)
        {
            return new Bitmap(this, g.Texture, g.SizeX, g.SizeY, false, false);
        }

        private T[] CopyResizeArray<T>(T[] array, int size)
        {
            var newArray = new T[size];
            Array.Copy(array, newArray, size);
            return newArray;
        }

        private FloatBuffer GetVtxBuffer(int size)
        {
            var roundedSize = Math.Max(MinBufferSize, Utils.NextPowerOfTwo(size));
            var idx = MaxBufferSizeLog2 - Utils.Log2Int(roundedSize);
            var buffer = (FloatBuffer)null;

            if (freeVtxBuffers[idx].Count == 0)
            {
                buffer = ByteBuffer.AllocateDirect(sizeof(float) * roundedSize).Order(ByteOrder.NativeOrder()).AsFloatBuffer();
            }
            else
            {
                var lastIdx = freeVtxBuffers[idx].Count - 1;
                buffer = freeVtxBuffers[idx][lastIdx];
                freeVtxBuffers[idx].RemoveAt(lastIdx);
            }

            usedVtxBuffers[idx].Add(buffer);
            buffer.Position(0);
            return buffer;
        }

        private IntBuffer GetColBuffer(int size)
        {
            var roundedSize = Math.Max(MinBufferSize, Utils.NextPowerOfTwo(size));
            var idx = MaxBufferSizeLog2 - Utils.Log2Int(roundedSize);
            var buffer = (IntBuffer)null;

            if (freeColBuffers[idx].Count == 0)
            {
                buffer = ByteBuffer.AllocateDirect(sizeof(int) * roundedSize).Order(ByteOrder.NativeOrder()).AsIntBuffer();
            }
            else
            {
                var lastIdx = freeColBuffers[idx].Count - 1;
                buffer = freeColBuffers[idx][lastIdx];
                freeColBuffers[idx].RemoveAt(lastIdx);
            }

            usedColBuffers[idx].Add(buffer);
            buffer.Position(0);
            return buffer;
        }

        private ShortBuffer GetIdxBuffer(int size)
        {
            var roundedSize = Math.Max(MinBufferSize, Utils.NextPowerOfTwo(size));
            var idx = MaxBufferSizeLog2 - Utils.Log2Int(roundedSize);
            var buffer = (ShortBuffer)null;

            if (freeIdxBuffers[idx].Count == 0)
            {
                buffer = ByteBuffer.AllocateDirect(sizeof(short) * roundedSize).Order(ByteOrder.NativeOrder()).AsShortBuffer();
            }
            else
            {
                var lastIdx = freeIdxBuffers[idx].Count - 1;
                buffer = freeIdxBuffers[idx][lastIdx];
                freeIdxBuffers[idx].RemoveAt(lastIdx);
            }

            usedIdxBuffers[idx].Add(buffer);
            buffer.Position(0);
            return buffer;
        }

        private FloatBuffer CopyGetVtxBuffer(float[] array, int size)
        {
            var newArray = new float[size];
            Array.Copy(array, newArray, size);
            var buffer = GetVtxBuffer(size);
            buffer.Put(newArray);
            buffer.Position(0);
            return buffer;
        }

        private IntBuffer CopyGetColBuffer(int[] array, int size)
        {
            var newArray = new int[size];
            Array.Copy(array, newArray, size);
            var buffer = GetColBuffer(size);
            buffer.Put(newArray);
            buffer.Position(0);
            return buffer;
        }

        private ShortBuffer CopyGetIdxBuffer(short[] array, int size)
        {
            var newArray = new short[size];
            Array.Copy(array, newArray, size);
            var buffer = GetIdxBuffer(size);
            buffer.Put(newArray);
            buffer.Position(0);
            return buffer;
        }

        public override unsafe void DrawCommandList(CommandList list, Rectangle scissor)
        {
            if (list == null)
                return;

            if (list.HasAnything)
            {
                if (!scissor.IsEmpty)
                    SetScissorRect(scissor.Left, scissor.Top, scissor.Right, scissor.Bottom);

                if (list.HasAnyMeshes)
                {
                    var drawData = list.GetMeshDrawData();

                    GLES11.GlEnableClientState(GLES11.GlColorArray);

                    foreach (var draw in drawData)
                    {
                        var vb = CopyGetVtxBuffer(draw.vtxArray, draw.vtxArraySize);
                        var cb = CopyGetColBuffer(draw.colArray, draw.colArraySize);
                        var ib = CopyGetIdxBuffer(draw.idxArray, draw.idxArraySize);

                        //if (draw.smooth) GLES11.GlEnable(GLES11.GlPolygonSmooth);
                        GLES11.GlColorPointer(4, GLES11.GlUnsignedByte, 0, cb);
                        GLES11.GlVertexPointer(2, GLES11.GlFloat, 0, vb);
                        GLES11.GlDrawElements(GLES11.GlTriangles, draw.numIndices, GLES11.GlUnsignedShort, ib);
                        //if (draw.smooth) GLES11.GlDisable(GLES11.GlPolygonSmooth);
                    }

                    GLES11.GlDisableClientState(GLES11.GlColorArray);
                }

                if (list.HasAnyLines)
                {
                    var drawData = list.GetLineDrawData();

                    GLES11.GlPushMatrix();
                    GLES11.GlTranslatef(0.5f, 0.5f, 0.0f);
                    GLES11.GlEnable(GLES11.GlTexture2d);
                    GLES11.GlBindTexture(GLES11.GlTexture2d, dashedBitmap.Id);
                    GLES11.GlEnableClientState(GLES11.GlColorArray);
                    GLES11.GlEnableClientState(GLES11.GlTextureCoordArray);

                    foreach (var draw in drawData)
                    {
                        var vb = CopyGetVtxBuffer(draw.vtxArray, draw.vtxArraySize);
                        var cb = CopyGetColBuffer(draw.colArray, draw.colArraySize);
                        var tb = CopyGetVtxBuffer(draw.texArray, draw.texArraySize);

                        if (draw.smooth) GLES11.GlEnable(GLES11.GlLineSmooth);
                        GLES11.GlLineWidth(draw.lineWidth);
                        GLES11.GlTexCoordPointer(2, GLES11.GlFloat, 0, tb);
                        GLES11.GlColorPointer(4, GLES11.GlUnsignedByte, 0, cb);
                        GLES11.GlVertexPointer(2, GLES11.GlFloat, 0, vb);
                        GLES11.GlDrawArrays(GLES11.GlLines, 0, draw.numVertices);
                        if (draw.smooth) GLES11.GlDisable(GLES11.GlLineSmooth);
                    }

                    GLES11.GlDisableClientState(GLES11.GlColorArray);
                    GLES11.GlDisableClientState(GLES11.GlTextureCoordArray);
                    GLES11.GlDisable(GLES11.GlTexture2d);
                    GLES11.GlPopMatrix();
                }

                if (list.HasAnyBitmaps)
                {
                    var drawData = list.GetBitmapDrawData(vtxArray, texArray, colArray, out var vtxSize, out var texSize, out var colSize, out var idxSize);

                    var vb = CopyGetVtxBuffer(vtxArray, vtxSize);
                    var cb = CopyGetColBuffer(colArray, colSize);
                    var tb = CopyGetVtxBuffer(texArray, texSize);
                    var ib = CopyGetIdxBuffer(quadIdxArray, idxSize);

                    GLES11.GlEnable(GLES11.GlTexture2d);
                    GLES11.GlEnableClientState(GLES11.GlColorArray);
                    GLES11.GlEnableClientState(GLES11.GlTextureCoordArray);
                    GLES11.GlTexCoordPointer(2, GLES11.GlFloat, 0, tb);
                    GLES11.GlColorPointer(4, GLES11.GlUnsignedByte, 0, cb);
                    GLES11.GlVertexPointer(2, GLES11.GlFloat, 0, vb);

                    foreach (var draw in drawData)
                    {
                        ib.Position(draw.start);
                        GLES11.GlBindTexture(GLES11.GlTexture2d, draw.textureId);
                        GLES11.GlDrawElements(GLES11.GlTriangles, draw.count, GLES11.GlUnsignedShort, ib);
                    }

                    GLES11.GlDisableClientState(GLES11.GlColorArray);
                    GLES11.GlDisableClientState(GLES11.GlTextureCoordArray);
                    GLES11.GlDisable(GLES11.GlTexture2d);
                }

                if (list.HasAnyTexts)
                {
                    var drawData = list.GetTextDrawData(vtxArray, texArray, colArray, out var vtxSize, out var texSize, out var colSize, out var idxSize);

                    var vb = CopyGetVtxBuffer(vtxArray, vtxSize);
                    var cb = CopyGetColBuffer(colArray, colSize);
                    var tb = CopyGetVtxBuffer(texArray, texSize);
                    var ib = CopyGetIdxBuffer(quadIdxArray, idxSize);

                    GLES11.GlEnable(GLES11.GlTexture2d);
                    GLES11.GlEnableClientState(GLES11.GlColorArray);
                    GLES11.GlEnableClientState(GLES11.GlTextureCoordArray);
                    GLES11.GlTexCoordPointer(2, GLES11.GlFloat, 0, tb);
                    GLES11.GlColorPointer(4, GLES11.GlUnsignedByte, 0, cb);
                    GLES11.GlVertexPointer(2, GLES11.GlFloat, 0, vb);

                    foreach (var draw in drawData)
                    {
                        ib.Position(draw.start);
                        GLES11.GlBindTexture(GLES11.GlTexture2d, draw.textureId);
                        GLES11.GlDrawElements(GLES11.GlTriangles, draw.count, GLES11.GlUnsignedShort, ib);
                    }

                    GLES11.GlDisableClientState(GLES11.GlColorArray);
                    GLES11.GlDisableClientState(GLES11.GlTextureCoordArray);
                    GLES11.GlDisable(GLES11.GlTexture2d);
                }

                if (!scissor.IsEmpty)
                    ClearScissorRect();
            }

            list.Release();
        }
    }

    public class OffscreenGraphics : Graphics
    {
        protected int fbo;
        protected int texture;
        protected int resX;
        protected int resY;

        public int Texture => texture;
        public int SizeX => resX;
        public int SizeY => resY;

        private OffscreenGraphics(int imageSizeX, int imageSizeY, bool allowReadback) : base(1.0f, 1.0f)
        {
            resX = imageSizeX;
            resY = imageSizeY;

            if (!allowReadback)
            {
                texture = CreateEmptyTexture(imageSizeX, imageSizeY, true, false);

                var fbos = new int[1];
                GLES11Ext.GlGenFramebuffersOES(1, fbos, 0);
                fbo = fbos[0];

                GLES11Ext.GlBindFramebufferOES(GLES11Ext.GlFramebufferOes, fbo);
                GLES11Ext.GlFramebufferTexture2DOES(GLES11Ext.GlFramebufferOes, GLES11Ext.GlColorAttachment0Oes, GLES11.GlTexture2d, texture, 0);
                GLES11Ext.GlBindFramebufferOES(GLES11Ext.GlFramebufferOes, 0);
            }
        }

        public static OffscreenGraphics Create(int imageSizeX, int imageSizeY, bool allowReadback)
        {
#if !DEBUG
            try
#endif
            {
                var extentions = GLES11.GlGetString(GLES11.GlExtensions);

                if (extentions.ToUpper().Contains("GL_OES_FRAMEBUFFER_OBJECT"))
                    return new OffscreenGraphics(imageSizeX, imageSizeY, allowReadback);
            }
#if !DEBUG
            catch
            {
            }
#endif

            return null;
        }

        public override void BeginDrawControl(Rectangle unflippedControlRect, int windowSizeY)
        {
            if (fbo > 0)
                GLES11Ext.GlBindFramebufferOES(GLES11Ext.GlFramebufferOes, fbo);

            base.BeginDrawControl(unflippedControlRect, windowSizeY);
        }

        public override void EndDrawControl()
        {
            base.EndDrawControl();

            if (fbo > 0)
                GLES11Ext.GlBindFramebufferOES(GLES11Ext.GlFramebufferOes, 0);
        }

        public unsafe void GetBitmap(byte[] data)
        {
            // Our rendering is fed directly to the encoder on Android.
        }

        public override void Dispose()
        {
            if (texture != 0) GLES11.GlDeleteTextures(1, new[] { texture }, 0);
            if (fbo     != 0) GLES11Ext.GlDeleteFramebuffersOES(1, new[] { fbo }, 0);

            base.Dispose();
        }
    };
}