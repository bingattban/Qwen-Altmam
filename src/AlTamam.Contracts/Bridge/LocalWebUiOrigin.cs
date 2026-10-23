namespace AlTamam.Contracts.Bridge;

/// <summary>The reserved, offline-only origin for the bundled UI assets.</summary>
/// <remarks>Configure CoreWebView2.SetVirtualHostNameToFolderMapping using this host
/// when the WPF desktop host is introduced. Never resolve it over the public internet.</remarks>
public static class LocalWebUiOrigin
{
    public const string Host = "manger.adwas.example";
    public const string BaseUrl = "https://manger.adwas.example/";
    // Virtual-host mapping resolves files by path; always navigate to a real HTML document.
    public const string EntryPointUrl = BaseUrl + "index.html";
}
