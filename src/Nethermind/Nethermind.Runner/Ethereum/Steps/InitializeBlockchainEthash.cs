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
using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Runner.Ethereum.Api;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitializeBlockchainEthash : InitializeBlockchain
    {
        private readonly EthashNethermindApi _api;
        private INethermindApi _nethermindApi => _api;

        public InitializeBlockchainEthash(EthashNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override void InitSealEngine()
        {
            _api.RewardCalculatorSource = new RewardCalculator(_api.SpecProvider);
            DifficultyCalculator difficultyCalculator = new DifficultyCalculator(_api.SpecProvider);
            _api.Sealer = _nethermindApi.Config<IInitConfig>().IsMining
                ? (ISealer) new EthashSealer(new Ethash(_api.LogManager), _api.EngineSigner, _api.LogManager)
                : NullSealEngine.Instance;
            _api.SealValidator = new EthashSealValidator(_api.LogManager, difficultyCalculator, _api.CryptoRandom, new Ethash(_api.LogManager));
        }
    }
}
