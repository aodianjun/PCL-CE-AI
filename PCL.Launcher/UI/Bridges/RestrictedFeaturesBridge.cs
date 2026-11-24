using PCL.Core.App.Localization;
using PCL.Core.App.Region;
using PCL.Core.UI.Hints;

namespace PCL.UI.Bridges;

/// <summary>
/// 区域限制功能。PCL CE-AI 不再按国家或地区禁用 Minecraft 下载/补全/整合包安装，
/// 因此这里始终放行；保留接口以兼容原有的调用点。
/// </summary>
public sealed class RestrictedFeaturesBridge : IRestrictedFeatures
{
    public bool IsRestricted => false;

    public bool Check(string feature) => true;

    public void ThrowIfRestricted(string feature)
    {
    }
}