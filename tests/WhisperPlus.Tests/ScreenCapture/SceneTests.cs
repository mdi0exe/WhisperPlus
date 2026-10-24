using SkiaSharp;
using WhisperPlus.ScreenCapture.Editor;
using Xunit;

namespace WhisperPlus.Tests.ScreenCapture;

/// <summary>
/// Covers the scene behaviours the overlay relies on: a blur has to be re-baked when what is
/// underneath it changes, and the undo history has to be able to report the items it keeps alive
/// (that is how <see cref="Scene.ReleaseResources"/> reaches them).
/// </summary>
public sealed class SceneTests
{
    private static Scene NewScene()
    {
        var scene = new Scene();
        scene.Items.Add(new RectItem { Bounds = new SKRect(10, 10, 60, 60) });
        var blur = new BlurItem { Bounds = new SKRect(0, 0, 200, 200), NeedsBake = false };
        scene.Items.Add(blur);
        return scene;
    }

    [Fact]
    public void MovingAnItemBelowABlur_MarksTheBlurForRebake()
    {
        var scene = NewScene();
        var rect = scene.Items[0];
        var blur = (BlurItem)scene.Items[1];

        // A baked blur holds the pixels it captured; nothing else clears that flag.
        Assert.False(blur.NeedsBake);

        var before = rect.Clone();
        rect.Move(40, 40);
        scene.Apply(new ModifyItemAction(rect, before, rect.Clone()));

        Assert.True(blur.NeedsBake);
    }

    [Fact]
    public void UndoingAMove_AlsoMarksTheBlurForRebake()
    {
        var scene = NewScene();
        var rect = scene.Items[0];
        var blur = (BlurItem)scene.Items[1];

        var before = rect.Clone();
        rect.Move(40, 40);
        scene.Apply(new ModifyItemAction(rect, before, rect.Clone()));

        // Pretend the blur has just been re-baked.
        blur.NeedsBake = false;

        scene.Undo();

        Assert.True(blur.NeedsBake);
    }

    [Fact]
    public void ABlurBelowTheChange_IsNotRebaked()
    {
        var scene = new Scene();
        var blur = new BlurItem { Bounds = new SKRect(0, 0, 200, 200), NeedsBake = false };
        var rect = new RectItem { Bounds = new SKRect(10, 10, 60, 60) };
        scene.Items.Add(blur);
        scene.Items.Add(rect);

        var before = rect.Clone();
        rect.Move(40, 40);
        scene.Apply(new ModifyItemAction(rect, before, rect.Clone()));

        // The blur sits *under* the rectangle, so its own capture is still correct.
        Assert.False(blur.NeedsBake);
    }

    [Fact]
    public void HistoryReportsTheItemsItKeepsAlive()
    {
        var scene = NewScene();
        var rect = scene.Items[0];

        var before = rect.Clone();
        rect.Move(10, 10);
        var action = new ModifyItemAction(rect, before, rect.Clone());
        scene.Apply(action);

        var referenced = action.ReferencedItems();
        Assert.Contains(rect, referenced);
        Assert.Contains(before, referenced);
    }

    [Fact]
    public void ReleaseResources_EmptiesTheHistoryAndIsIdempotent()
    {
        var scene = NewScene();
        var rect = scene.Items[0];
        var before = rect.Clone();
        rect.Move(10, 10);
        scene.Apply(new ModifyItemAction(rect, before, rect.Clone()));

        Assert.True(scene.CanUndo);

        scene.ReleaseResources();
        scene.ReleaseResources(); // must not throw on a second call

        Assert.False(scene.CanUndo);
        Assert.False(scene.CanRedo);
        Assert.Null(scene.Background);
    }

    [Fact]
    public void ReorderAction_MarksBlursForRebake_AndReportsReferencedItems()
    {
        var scene = new Scene();
        var rect = new RectItem { Bounds = new SKRect(10, 10, 60, 60) };
        var blur = new BlurItem { Bounds = new SKRect(0, 0, 200, 200), NeedsBake = false };
        scene.Items.Add(rect);
        scene.Items.Add(blur);

        var before = new List<SceneItem>(scene.Items);
        var after = new List<SceneItem> { blur, rect };
        var action = new ReorderAction(before, after);

        Assert.NotEmpty(action.ReferencedItems());

        scene.Apply(action);
        Assert.True(blur.NeedsBake);

        blur.NeedsBake = false;
        scene.Undo();
        Assert.True(blur.NeedsBake);
    }

    [Fact]
    public void ArrowItem_HitTest_BentArrowSamplesCurve()
    {
        var arrow = new ArrowItem
        {
            Start = new SKPoint(0, 0),
            End = new SKPoint(100, 0),
            BendPoint = new SKPoint(50, 50),
            StrokeWidth = 4f,
        };
        arrow.SyncBounds();

        // The midpoint of quadratic Bezier with Start=(0,0), End=(100,0), CP=(50,50) at t=0.5:
        // x = 0.25*0 + 0.5*50 + 0.25*100 = 50
        // y = 0.25*0 + 0.5*50 + 0.25*0 = 25
        Assert.True(arrow.HitTest(new SKPoint(50, 25)));

        // The straight unbent line midpoint (50, 0) should NOT hit the bent curve (tolerance ~6px)
        Assert.False(arrow.HitTest(new SKPoint(50, 0)));
    }

    [Fact]
    public void ImageItem_RestoreFrom_ClonesBitmap()
    {
        var info = new SKImageInfo(10, 10, SKColorType.Rgba8888);
        using var originalBmp = new SKBitmap(info);
        originalBmp.Erase(SKColors.Red);

        var item = new ImageItem { Bitmap = originalBmp };

        using var otherBmp = new SKBitmap(info);
        otherBmp.Erase(SKColors.Blue);
        var otherItem = new ImageItem { Bitmap = otherBmp };

        item.RestoreFrom(otherItem);

        // Bitmaps should not share the exact same object reference
        Assert.NotSame(otherBmp, item.Bitmap);
        Assert.Equal(otherBmp.Width, item.Bitmap.Width);
    }
}
