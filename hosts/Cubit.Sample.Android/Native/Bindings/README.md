# Android SDL 资产来源

此目录不再存放或手工引用 `Silk.NET.Windowing.Sdl.dll`。Android 示例只通过项目级
`NativePackages` 源还原本地修订包：

```text
Silk.NET.Windowing.Sdl.2.23.1-cubit.1.nupkg
SHA-256: AAF02F4A35BC734B9ADE29C9D8B258CB4DCEF1E12EBDEFAF3341A479C9050217
```

该包保留 Silk 的 Android 托管绑定 DLL 与关联 AAR，只将 AAR 内 SDL 2.32.10 的四 ABI
原生库重建为 16 KB 页对齐版本。不得恢复手工 `<Reference>`、`AndroidLibrary` 或裸 AAR
接入；`androidpackagecheck`、`androidnativeaudit` 和 `androidreleasecheck` 共同验证包、
锁文件、实际 APK 资产和 `PT_LOAD=0x4000` 对齐。
