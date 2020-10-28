﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RailworksDownloader
{
    class RouteInfo
    {
        public string Name { get; set; }

        public string Path { get; set; }

        internal RouteInfo(string name, string path)
        {
            Name = name;
            Path = path;
        }
    }
}
