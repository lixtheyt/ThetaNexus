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

        internal enum MainListImageShorts
        {
            Repository,
            Tag,
            Size,
            Created,
            Used
        }
    }
}