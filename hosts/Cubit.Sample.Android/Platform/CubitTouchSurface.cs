namespace Cubit.Sample.Android.Platform;

/// <summary>
/// SDL 的 SurfaceView。触摸由 Activity 在子视图分发前统一路由，
/// 此处只负责在 SDL 布局就绪后挂载可视操控层。
/// </summary>
internal sealed class CubitTouchSurface : global::Org.Libsdl.App.SDLSurface
{
    private readonly Action<global::Android.Views.ViewGroup> _onAttachedToSdlLayout;

    public CubitTouchSurface(
        global::Android.Content.Context context,
        Action<global::Android.Views.ViewGroup> onAttachedToSdlLayout)
        : base(context)
    {
        _onAttachedToSdlLayout = onAttachedToSdlLayout;
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        if (Parent is global::Android.Views.ViewGroup layout)
        {
            _onAttachedToSdlLayout(layout);
        }
    }

}
