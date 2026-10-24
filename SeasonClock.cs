namespace BetterKingdom
{
    /// <summary>季节时钟：由统治累计天数推算当前季节与季节内天数（每季 16 天）。</summary>
    internal static class SeasonClock
    {
        internal const int DaysPerSeason = 16;

        /// <summary>统治累计天数（换岛不重置，从 1 开始）；取不到时回退到导演当前天数，均失败返回 0。</summary>
        internal static int GetDayNumber(Director director)
        {
            try
            {
                CampaignSaveData save = CampaignSaveData.current;
                if (save != null)
                {
                    int day = save.TotalGameDays + 1;
                    if (day > 0) return day;
                }
            }
            catch { }

            try
            {
                if (director != null)
                {
                    int day = director.CurrentDaysSinceFirstLandingThisReign;
                    if (day > 0) return day;
                }
            }
            catch { }

            return 0;
        }

        /// <summary>按累计天数推算季节（1=夏、2=秋、4=冬、8=春）；以秋天开局时整体后移两个季度。</summary>
        internal static Season GetSeason(Director director, int day)
        {
            if (day > 0)
            {
                int index = (day - 1) / DaysPerSeason;
                try
                {
                    if (director != null && director.startGameInAutumn) index += 2;
                }
                catch { }

                switch (index % 4)
                {
                    case 0: return (Season)8;   // 春
                    case 1: return (Season)1;   // 夏
                    case 2: return (Season)2;   // 秋
                    default: return (Season)4;  // 冬
                }
            }

            try
            {
                if (director != null) return director.CurrentSeason;
            }
            catch { }

            return (Season)8;
        }

        /// <summary>季节内第几天（1-16）；取不到累计天数时回退到导演当前季节天数。</summary>
        internal static int GetSeasonDay(Director director, int day)
        {
            if (day > 0) return (day - 1) % DaysPerSeason + 1;

            try
            {
                if (director != null) return director.CurrentSeasonDay;
            }
            catch { }

            return 0;
        }
    }
}
