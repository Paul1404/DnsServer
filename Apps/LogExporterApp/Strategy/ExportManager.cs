﻿/*
Technitium DNS Server
Copyright (C) 2024  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LogExporter.Strategy
{
    public class ExportManager
    {
        #region variables

        private readonly List<IExportStrategy> _exportStrategies;

        #endregion variables

        #region constructor

        public ExportManager()
        {
            _exportStrategies = new List<IExportStrategy>();
        }

        #endregion constructor

        #region public

        public async Task ImplementStrategyForAsync(List<LogEntry> logs, CancellationToken cancellationToken = default)
        {
            foreach (var strategy in _exportStrategies)
            {
                await strategy.ExportLogsAsync(logs, cancellationToken).ConfigureAwait(false);
            }
        }

        public void RegisterStrategy(IExportStrategy strategy)
        {
            _exportStrategies.Add(strategy);
        }

        #endregion public
    }
}