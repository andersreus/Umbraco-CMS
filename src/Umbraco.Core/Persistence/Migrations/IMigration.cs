﻿using Umbraco.Core.Plugins;

namespace Umbraco.Core.Persistence.Migrations
{
    /// <summary>
    /// Marker interface for database migrations
    /// </summary>
    public interface IMigration : IDiscoverable
    {
        void Up();
        void Down();
    }
}