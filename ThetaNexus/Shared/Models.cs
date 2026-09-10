namespace ThetaNexus.Shared
{
    internal static class Models
    {
        internal enum MainListSections
        {
            Containers,
            Images,
            Volumes,
            Networks,
            Events
        }

        internal enum MainListSorts
        {
            Name,
            State,
            Image,
            Ports,
            Cpu,
            Mem,
            Age
        }

        internal enum Outcome
        {
            Succeeded,
            Warned,
            Failed
        }

        internal enum DetailsTabs
        {
            Overview,
            Env,
            Mounts,
            Networks,
            Health
        }

        internal enum ImageInfoTabs
        {
            Overview,
            Layers,
            Env,
            Labels
        }

        internal enum StatsTabs
        {
            Cpu,
            Memory,
            Network,
            Disk
        }

        internal enum MainListImageSorts
        {
            Repository,
            Tag,
            Size,
            Created,
            Used
        }

        internal enum MainListVolumeSorts
        {
            Name,
            Driver,
            Mounted,
            Created
        }

        internal enum MainListNetworkSorts
        {
            Name,
            Driver,
            Subnet,
            Containers
        }
    }
}