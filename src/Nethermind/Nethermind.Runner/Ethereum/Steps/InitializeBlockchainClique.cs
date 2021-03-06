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

using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Runner.Ethereum.Api;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitializeBlockchainClique : InitializeBlockchain
    {
        private readonly CliqueNethermindApi _api;
        private INethermindApi _nethermindApi => _api;

        public InitializeBlockchainClique(CliqueNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override void InitSealEngine()
        {
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.EthereumEcdsa == null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));
        
            _api.RewardCalculatorSource = NoBlockRewards.Instance;
            CliqueConfig cliqueConfig = new CliqueConfig {BlockPeriod = _api.ChainSpec.Clique.Period, Epoch = _api.ChainSpec.Clique.Epoch};
            _api.SnapshotManager = new SnapshotManager(cliqueConfig, _api.DbProvider.BlocksDb, _api.BlockTree, _api.EthereumEcdsa, _api.LogManager);
            _api.SealValidator = new CliqueSealValidator(cliqueConfig, _api.SnapshotManager, _api.LogManager);
            _api.RecoveryStep = new CompositeDataRecoveryStep(_api.RecoveryStep, new AuthorRecoveryStep(_api.SnapshotManager));
            if (_nethermindApi.Config<IInitConfig>().IsMining)
            {
                _api.Sealer = new CliqueSealer(_api.EngineSigner, cliqueConfig, _api.SnapshotManager, _api.LogManager);
            }
            else
            {
                ((NethermindApi)_api).Sealer = NullSealEngine.Instance;
            }
        }
    }
}
