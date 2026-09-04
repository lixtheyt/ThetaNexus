using System;
using System.Collections.Generic;
using System.Text;

namespace ThetaNexus.Shared
{
    internal class Models
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

        internal enum DetailsTabs
        {
            Overview,
            Env,
            Mounts,
            Networks,
            Health
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
