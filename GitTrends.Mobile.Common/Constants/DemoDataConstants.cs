﻿using System;

namespace GitTrends.Mobile.Common
{
    public static class DemoDataConstants
    {
        public const int RepoCount = 50;
        public const int ReferringSitesCount = 11;

        public const int MaximumRandomNumber = 10000;

        const string _loremIpsum = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum ";

        static readonly Random _random = new Random((int)DateTime.Now.Ticks);

        public static int GetRandomNumber() => _random.Next(MaximumRandomNumber);

        public static string GetRandomText()
        {
            var startIndex = _random.Next(_loremIpsum.Length / 2);
            var length = _random.Next(_loremIpsum.Length - 1 - startIndex);

            return _loremIpsum.Substring(startIndex, length);
        }

        //https://stackoverflow.com/a/194870/5953643
        public static DateTimeOffset GetRandomDate()
        {
            var gitHubFoundedDate = new DateTimeOffset(2008, 2, 8, 0, 0, 0, TimeSpan.Zero);

            int range = (DateTime.Today - gitHubFoundedDate).Days;

            return gitHubFoundedDate.AddDays(_random.Next(range));
        }
    }
}
