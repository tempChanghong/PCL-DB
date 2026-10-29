using System;
using System.Collections.Generic;
using System.Linq;

namespace PclDownloadBridge.Core
{

    [Flags]
    public enum ModpackPlatform
    {
        CurseForge = 1,
        Modrinth = 2,
        All = 3
    }

    // PCL 5ef81e4 PageResource / ResourceSearcher / ResourceVersion. Catalogue
    // identities are platform-qualified and never inferred from a display name.
    public sealed class ModpackQuery
    {
        public CommunityResourceKind Kind { get; set; } = CommunityResourceKind.Modpack;
        public string Loader { get; set; } = "";
        public string Text { get; set; } = "";
        public string Minecraft { get; set; } = "";
        public string Category { get; set; } = "/";
        public ModpackPlatform Platforms { get; set; } = ModpackPlatform.All;
        public ModpackQuery Copy()
        {
            return (ModpackQuery)MemberwiseClone();
        }
    }

    public sealed class ModpackProject
    {
        public CommunityResourceKind Kind { get; set; } = CommunityResourceKind.Modpack;
        public string PreferredVersionId { get; set; }
        public ModpackPlatform Platform { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Slug { get; set; }
        public string Summary { get; set; }
        public string IconUrl { get; set; }
        public string Website { get; set; }
        public long Downloads { get; set; }
        public DateTimeOffset Updated { get; set; }
        public string[] GameVersions { get; set; } = Array.Empty<string>();
        // Advisory project declarations. File compatibility and install identity
        // continue to come exclusively from the selected ModpackRelease/archive.
        public string[] Tags { get; set; } = Array.Empty<string>();
        public string[] Loaders { get; set; } = Array.Empty<string>();
        public string Key
        {
            get
            {
                return Platform.ToString() + ":" + Id + (Kind == CommunityResourceKind.Modpack ? "" : ":" + Kind.ToString());
            }
        }
    }

    public sealed class ModpackRelease
    {
        public CommunityResourceKind Kind { get; set; } = CommunityResourceKind.Modpack;
        public ResourceDependency[] Dependencies { get; set; } = Array.Empty<ResourceDependency>();
        public ModpackPlatform Platform { get; set; }
        public string ProjectId { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public DateTimeOffset Published { get; set; }
        // Advisory platform statistics; absent or malformed counters cannot invalidate
        // an otherwise verified archive. A known zero remains distinct from unknown.
        public long? Downloads { get; set; }
        public string ReleaseType { get; set; }
        public string[] GameVersions { get; set; } = Array.Empty<string>();
        // Missing compatibility metadata may defer to the archive, but an explicitly
        // unusable declaration must remain distinguishable from missing metadata.
        public string GameVersionMetadataError { get; set; }
        public string[] Loaders { get; set; } = Array.Empty<string>();
        public string FileName { get; set; }
        public string Url { get; set; }
        public long Size { get; set; }
        public string Sha1 { get; set; }
        public string Sha512 { get; set; }
        public string Md5 { get; set; }
        public ModpackRelease Copy()
        {
            ModpackRelease result = (ModpackRelease)MemberwiseClone();
            result.GameVersions = (string[])GameVersions.Clone();
            result.Loaders = (string[])Loaders.Clone();
            result.Dependencies = Dependencies.Select(item => new ResourceDependency() { ProjectId = item.ProjectId, VersionId = item.VersionId }).ToArray();
            return result;
        }
    }

    public sealed class ModpackCatalogPage
    {
        public List<ModpackProject> Projects { get; set; } = new List<ModpackProject>();
        public int Returned { get; set; }
        public long Total { get; set; }
    }

}
