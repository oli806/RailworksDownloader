﻿namespace RailworksDownloader
{
    public class DependencyPackage
    {
        public string Name { get; set; }
        
        public string PrettyState { 
            get { 
                switch (State)
                {
                    case DependencyState.Available:
                        return Localization.Strings.DepStateAvail;
                    case DependencyState.Unavailable:
                        return Localization.Strings.DepStateUnav;
                    case DependencyState.Paid:
                        return Localization.Strings.DepStatePaid;
                    case DependencyState.Downloaded:
                        return Localization.Strings.DepStateDown;
                    default:
                        return Localization.Strings.DepStateUnk;
                }
            } 
        }

        public DependencyState State { get; set; }

        public DependencyPackage(string name) : this(name, DependencyState.Unknown) { }

        public DependencyPackage(string name, DependencyState state)
        {
            Name = name;
            State = state;
        }
    }
}