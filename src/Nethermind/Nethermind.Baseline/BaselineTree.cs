using System;
using System.Runtime.CompilerServices;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

[assembly: InternalsVisibleTo("Nethermind.Baseline.Test")]

namespace Nethermind.Baseline
{
    public abstract class BaselineTree
    {
        private const int LeafRow = 32;
        private const int LeafLevel = 0;
        public const int TreeHeight = 32;
        private const ulong FirstLeafIndexAsNodeIndex = MaxNodes / 2;
        private const ulong MaxNodes = (1ul << (TreeHeight + 1)) - 1ul;
        private const ulong MaxNodeIndex = MaxNodes - 1;

        private readonly IKeyValueStore _keyValueStore;
        private protected int TruncationLength { get; }

        /* baseline does not use a sparse merkle tree - instead they use a single zero hash value
           does it expose any attack vectors? */
        public static Bytes32 ZeroHash = new Bytes32();

        /// <summary>
        /// Key for the storage value
        /// </summary>
        private static byte[] _countKey;

        static BaselineTree()
        {
            _countKey = Rlp.Encode(ulong.MaxValue).Bytes;
        }

        public uint Count { get; set; }

        public BaselineTree(IKeyValueStore keyValueStore, int truncationLength = 0)
        {
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            TruncationLength = truncationLength;

            byte[] countBytes = _keyValueStore[_countKey];
            Count = countBytes == null ? 0 : new RlpStream(countBytes).DecodeUInt();
        }

        private void StoreCountInTheDb()
        {
            _keyValueStore[_countKey] = Rlp.Encode(Count).Bytes;
        }

        private Bytes32 LoadValue(uint row, uint indexAtRow)
        {
            return LoadValue(GetNodeIndex(row, indexAtRow));
        }

        private void SaveValue(ulong nodeIndex, byte[] hashBytes)
        {
            _keyValueStore[Rlp.Encode(nodeIndex).Bytes] = hashBytes;
        }

        private void SaveValue(ulong nodeIndex, Bytes32 hash)
        {
            SaveValue(nodeIndex, hash.AsSpan().ToArray());
        }

        internal static uint GetRow(ulong nodeIndex)
        {
            ValidateNodeIndex(nodeIndex);
            for (uint row = 0u; row < LeafRow; row++)
            {
                if (2ul << (int) row >= nodeIndex + 2ul)
                {
                    return row;
                }
            }

            return 31u;
        }

        private Bytes32 LoadValue(ulong nodeIndex)
        {
            byte[] nodeHashBytes = _keyValueStore[Rlp.Encode(nodeIndex).Bytes];
            if (nodeHashBytes == null)
            {
                return ZeroHash;
            }

            return Bytes32.Wrap(nodeHashBytes);
        }

        internal static uint GetIndexAtRow(uint row, ulong nodeIndex)
        {
            ValidateRow(row);
            ValidateNodeIndex(row, nodeIndex);

            uint indexAtRow = (uint) (nodeIndex - ((1ul << (int) row) - 1ul));
            ValidateIndexAtRow(row, indexAtRow);
            return indexAtRow;
        }

        internal static uint GetLeafIndex(ulong nodeIndex)
        {
            ValidateNodeIndex(LeafRow, nodeIndex);
            return (uint) (nodeIndex - FirstLeafIndexAsNodeIndex);
        }

        internal static uint GetSiblingIndexAtRow(uint row, uint indexAtRow)
        {
            ValidateRow(row);
            ValidateIndexAtRow(row, indexAtRow);

            if (row == 0)
            {
                throw new ArgumentOutOfRangeException("Root node has no siblings.");
            }

            if (indexAtRow % 2 == 0)
            {
                return indexAtRow + 1;
            }

            return indexAtRow - 1;
        }

        internal static void ValidateNodeIndex(ulong nodeIndex)
        {
            if (nodeIndex > MaxNodeIndex)
            {
                throw new ArgumentOutOfRangeException($"Node index should be between 0 and {MaxNodeIndex}");
            }
        }

        internal static void ValidateRow(uint row)
        {
            if (row > LeafRow)
            {
                throw new ArgumentOutOfRangeException($"Tree row should be between 0 and {LeafRow}");
            }
        }

        private static ulong GetMinNodeIndex(uint row)
        {
            return (1ul << (int) row) - 1;
        }

        private static ulong GetMaxNodeIndex(uint row)
        {
            return (1ul << (int) (row + 1u)) - 2;
        }

        private static void ValidateNodeIndex(uint row, ulong nodeIndex)
        {
            ulong minNodeIndex = GetMinNodeIndex(row);
            ulong maxNodeIndex = GetMaxNodeIndex(row);

            if (nodeIndex < minNodeIndex || nodeIndex > maxNodeIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(nodeIndex), $"Node index at row {row} should be in the range of [{minNodeIndex},{maxNodeIndex}] and was {nodeIndex}");
            }
        }

        internal static void ValidateIndexAtRow(uint row, uint indexAtRow)
        {
            uint maxIndexAtRow = (uint) (1ul << (int) row) - 1u;
            if (indexAtRow > maxIndexAtRow)
            {
                throw new ArgumentOutOfRangeException($"Tree row {row} should only has indices between 0 and {maxIndexAtRow}");
            }
        }

        internal static ulong GetNodeIndex(uint row, uint indexAtRow)
        {
            ValidateRow(row);
            ValidateIndexAtRow(row, indexAtRow);

            return (1ul << (int) row) - 1ul + indexAtRow;
        }

        internal static ulong GetParentIndex(ulong nodeIndex)
        {
            ValidateNodeIndex(nodeIndex);

            if (nodeIndex == 0ul)
            {
                throw new ArgumentOutOfRangeException("Root node has no parent");
            }

            return (nodeIndex + 1ul) / 2ul - 1ul;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Insert(Bytes32 leaf)
        {
            _keyValueStore[Rlp.Encode(Count).Bytes] = leaf.AsSpan().ToArray();

            uint indexAtRow = Count;
            uint siblingIndexAtRow = GetSiblingIndexAtRow(LeafRow, indexAtRow);
            Bytes32 hash = leaf;
            Bytes32 siblingHash = LoadValue(LeafRow, siblingIndexAtRow);

            ulong nodeIndex = GetNodeIndex(LeafRow, Count);
            SaveValue(nodeIndex, hash);

            for (int row = LeafRow; row > 0; row--)
            {
                ulong parentIndex = GetParentIndex(GetNodeIndex((uint) row, indexAtRow));
                var parentHash = Hash(hash.AsSpan(), siblingHash.AsSpan());
                SaveValue(parentIndex, parentHash);

                indexAtRow = GetIndexAtRow((uint) row - 1, parentIndex);

                if (row != 1)
                {
                    siblingIndexAtRow = GetSiblingIndexAtRow((uint) row - 1, indexAtRow);
                    hash = Bytes32.Wrap(parentHash);
                    siblingHash = LoadValue((uint) row - 1, siblingIndexAtRow);
                }
            }

            Count++;
            StoreCountInTheDb();
        }

        public BaselineTreeNode[] GetProof(uint leafIndex)
        {
            ValidateIndexAtRow(LeafRow, leafIndex);

            BaselineTreeNode[] proof = new BaselineTreeNode[TreeHeight];

            uint indexAtRow = leafIndex;
            for (int proofRow = TreeHeight; proofRow > 0; proofRow--)
            {
                uint siblingIndex = GetSiblingIndexAtRow((uint) proofRow, indexAtRow);
                ulong siblingNodeIndex = GetNodeIndex((uint) proofRow, siblingIndex);
                ulong nodeIndex = GetNodeIndex((uint) proofRow, indexAtRow);
                proof[TreeHeight - proofRow] = new BaselineTreeNode(LoadValue(siblingNodeIndex), siblingNodeIndex);
                indexAtRow = GetIndexAtRow((uint) proofRow - 1u, GetParentIndex(nodeIndex));
            }

            return proof;
        }

        protected abstract byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);
    }
}