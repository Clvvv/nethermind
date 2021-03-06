﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Stats;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies]
    public class InitializeNodeStats : IStep
    {
        private readonly INethermindApi _api;

        public InitializeNodeStats(INethermindApi api)
        {
            _api = api;
        }

        public Task Execute(CancellationToken _)
        {
            // create shared objects between discovery and peer manager
            IStatsConfig statsConfig = _api.Config<IStatsConfig>();
            _api.NodeStatsManager = new NodeStatsManager(statsConfig, _api.LogManager);

            return Task.CompletedTask;
        }
    }
}