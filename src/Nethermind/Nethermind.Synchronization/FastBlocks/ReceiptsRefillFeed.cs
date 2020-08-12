//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class ReceiptsRefillFeed : SyncFeed<ReceiptsSyncBatch?>, IReceiptRefill
    {
        private int _requestSize = GethSyncLimits.MaxReceiptFetch;

        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISyncPeerPool _syncPeerPool;

        private SyncStatusList? _syncStatusList;

        public ReceiptsRefillFeed(
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ISyncPeerPool syncPeerPool,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.Receipts;

        private bool ShouldBuildANewBatch()
        {
            if (_syncStatusList != null)
            {
                if (_syncStatusList.IsComplete)
                {
                    _syncStatusList = null;
                }
            }

            return _syncStatusList != null;
        }

        public void Refill(long startBlockNumber, long endBlockNumber)
        {
            if (_syncStatusList == null)
            {
                _syncStatusList = new SyncStatusList(_blockTree, startBlockNumber, endBlockNumber, null);
            }
            else
            {
                throw new InvalidOperationException("Receipt refill in progress");
            }
        }
        
        public override Task<ReceiptsSyncBatch?> PrepareRequest()
        {
            ReceiptsSyncBatch? batch = null;
            if (ShouldBuildANewBatch())
            {
                BlockInfo[] infos = new BlockInfo[_requestSize];
                _syncStatusList!.GetInfosForBatch(infos);
                if (infos[0] != null)
                {
                    batch = new ReceiptsSyncBatch(infos);
                    batch.MinNumber = infos[0].BlockNumber;
                    batch.Prioritized = true;
                }
            }

            return Task.FromResult(batch);
        }

        public override SyncResponseHandlingResult HandleResponse(ReceiptsSyncBatch? batch)
        {
            batch?.MarkHandlingStart();
            try
            {
                if (batch == null)
                {
                    if (_logger.IsDebug) _logger.Debug("Received a NULL batch as a response");
                    return SyncResponseHandlingResult.InternalError;
                }

                int added = InsertReceipts(batch);
                return added == 0 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.OK;
            }
            finally
            {
                batch?.MarkHandlingEnd();
            }
        }

        private bool TryPrepareReceipts(BlockInfo blockInfo, TxReceipt[] receipts, out TxReceipt[]? preparedReceipts)
        {
            BlockHeader? header = _blockTree.FindHeader(blockInfo.BlockHash);
            if (header == null)
            {
                if (_logger.IsWarn) _logger.Warn("Could not find header for requested blockhash.");
                preparedReceipts = null;
            }
            else
            {
                if (header.ReceiptsRoot == Keccak.EmptyTreeHash)
                {
                    preparedReceipts = receipts.Length == 0 ? receipts : null;
                }
                else
                {
                    Keccak receiptsRoot = new ReceiptTrie(blockInfo.BlockNumber, _specProvider, receipts).RootHash;
                    if (receiptsRoot != header.ReceiptsRoot)
                    {
                        preparedReceipts = null;
                    }
                    else
                    {
                        preparedReceipts = receipts;
                    }
                }
            }

            return preparedReceipts != null;
        }

        private int InsertReceipts(ReceiptsSyncBatch batch)
        {
            bool hasBreachedProtocol = false;
            int validResponsesCount = 0;

            for (int i = 0; i < batch.Infos.Length; i++)
            {
                BlockInfo? blockInfo = batch.Infos[i];
                TxReceipt[]? receipts = (batch.Response?.Length ?? 0) <= i
                    ? null
                    : (batch.Response![i] ?? Array.Empty<TxReceipt>());

                if (receipts != null)
                {
                    TxReceipt[]? prepared = null;
                    // last batch
                    if (blockInfo == null)
                    {
                        break;
                    }

                    bool isValid = !hasBreachedProtocol && TryPrepareReceipts(blockInfo, receipts, out prepared);
                    if (isValid)
                    {
                        Block block = _blockTree.FindBlock(blockInfo.BlockHash);
                        if (block == null)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Could not find block {blockInfo.BlockHash}");
                            if (_logger.IsWarn) _logger.Warn($"Could not find block {blockInfo.BlockHash}");
                            _syncStatusList!.MarkUnknown(blockInfo.BlockNumber);
                        }
                        else
                        {
                            try
                            {
                                _receiptStorage.Insert(block, prepared);
                                _syncStatusList!.MarkInserted(block.Number);
                                validResponsesCount++;
                            }
                            catch (InvalidDataException)
                            {
                                _syncStatusList!.MarkUnknown(blockInfo.BlockNumber);
                            }
                        }
                    }
                    else
                    {
                        hasBreachedProtocol = true;
                        if (_logger.IsDebug) _logger.Debug($"{batch} - reporting INVALID - tx or ommers");

                        if (batch.ResponseSourcePeer != null)
                        {
                            _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, "invalid tx or ommers root");
                        }

                        _syncStatusList!.MarkUnknown(blockInfo.BlockNumber);
                    }
                }
                else
                {
                    if (blockInfo != null)
                    {
                        _syncStatusList!.MarkUnknown(blockInfo.BlockNumber);
                    }
                }
            }

            AdjustRequestSize(batch, validResponsesCount);
            LogPostProcessingBatchInfo(batch, validResponsesCount);

            return validResponsesCount;
        }

        private void LogPostProcessingBatchInfo(ReceiptsSyncBatch batch, int validResponsesCount)
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"{nameof(ReceiptsSyncBatch)} back from {batch.ResponseSourcePeer} with {validResponsesCount}/{batch.Infos.Length}");
        }

        private void AdjustRequestSize(ReceiptsSyncBatch batch, int validResponsesCount)
        {
            lock (_syncStatusList!)
            {
                if (validResponsesCount == batch.Infos.Length)
                {
                    _requestSize = Math.Min(256, _requestSize * 2);
                }

                if (validResponsesCount == 0)
                {
                    _requestSize = Math.Max(4, _requestSize / 2);
                }
            }
        }
    }
}