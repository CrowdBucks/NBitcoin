﻿#if !NOSOCKET
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NBitcoin.SPV
{
	public class TrackerScanPosition
	{
		public BlockLocator Locator
		{
			get;
			set;
		}
		public DateTimeOffset From
		{
			get;
			set;
		}
	}
	/// <summary>
	/// Load a bloom filter on the node, and scan the blockchain
	/// </summary>
	public class TrackerBehavior : NodeBehavior, ICloneable
	{
		Tracker _Tracker;
		ConcurrentChain _Chain;
		ConcurrentChain _ExplicitChain;
		/// <summary>
		/// Create a new TrackerBehavior instance
		/// </summary>
		/// <param name="tracker">The Tracker registering transactions and confirmations</param>
		/// <param name="chain">The chain used to fetch height of incoming blocks, if null, use the chain of ChainBehavior</param>
		public TrackerBehavior(Tracker tracker, ConcurrentChain chain = null)
		{
			if(tracker == null)
				throw new ArgumentNullException("tracker");
			FalsePositiveRate = 0.0005;
			_Chain = chain;
			_ExplicitChain = chain;
			_Tracker = tracker;
		}

		public Tracker Tracker
		{
			get
			{
				return _Tracker;
			}
		}

		protected override void AttachCore()
		{
			if(_Chain == null)
			{
				var chainBehavior = AttachedNode.Behaviors.Find<ChainBehavior>();
				if(chainBehavior == null)
					throw new InvalidOperationException("A chain should either be passed in the constructor of TrackerBehavior, or a ChainBehavior should be attached on the node");
				_Chain = chainBehavior.Chain;
			}
			if(AttachedNode.State == Protocol.NodeState.HandShaked)
				SetBloomFilter();
			AttachedNode.StateChanged += AttachedNode_StateChanged;
			AttachedNode.MessageReceived += AttachedNode_MessageReceived;
		}

		void AttachedNode_StateChanged(Protocol.Node node, Protocol.NodeState oldState)
		{
			if(node.State == Protocol.NodeState.HandShaked)
				SetBloomFilter();
		}

		protected override void DetachCore()
		{
			AttachedNode.StateChanged -= AttachedNode_StateChanged;
			AttachedNode.MessageReceived -= AttachedNode_MessageReceived;
		}

		BoundedDictionary<uint256, MerkleBlock> _TransactionsToBlock = new BoundedDictionary<uint256, MerkleBlock>(1000);
		BoundedDictionary<uint256, Transaction> _KnownTxs = new BoundedDictionary<uint256, Transaction>(1000);
		void AttachedNode_MessageReceived(Protocol.Node node, Protocol.IncomingMessage message)
		{
			var merkleBlock = message.Message.Payload as MerkleBlockPayload;
			if(merkleBlock != null)
			{
				foreach(var txId in merkleBlock.Object.PartialMerkleTree.GetMatchedTransactions())
				{
					_TransactionsToBlock.AddOrUpdate(txId, merkleBlock.Object, (k, v) => merkleBlock.Object);
					var tx = _Tracker.GetKnownTransaction(txId);
					if(tx != null)
						Notify(tx, merkleBlock.Object);
				}
			}

			var invs = message.Message.Payload as InvPayload;
			if(invs != null)
			{
				foreach(var inv in invs)
				{
					if(inv.Type == InventoryType.MSG_BLOCK)
						node.SendMessage(new GetDataPayload(new InventoryVector(InventoryType.MSG_FILTERED_BLOCK, inv.Hash)));
					if(inv.Type == InventoryType.MSG_TX)
						node.SendMessage(new GetDataPayload(inv));
				}
			}

			var txPayload = message.Message.Payload as TxPayload;
			if(txPayload != null)
			{
				var tx = txPayload.Object;
				MerkleBlock blk;
				_TransactionsToBlock.TryGetValue(tx.GetHash(), out blk);
				Notify(tx, blk);
			}
		}

		private void Notify(Transaction tx, MerkleBlock blk)
		{
			if(blk == null)
			{
				_Tracker.NotifyTransaction(tx);
			}
			else
			{
				var prev = _Chain.GetBlock(blk.Header.HashPrevBlock);
				if(prev != null)
				{
					var header = new ChainedBlock(blk.Header, null, prev);
					_Tracker.NotifyTransaction(tx, header, blk);
				}
				else
				{
					_Tracker.NotifyTransaction(tx);
				}
			}
		}

		public double FalsePositiveRate
		{
			get;
			set;
		}


		void SetBloomFilter()
		{
			var node = AttachedNode;
			if(node != null)
			{
				var filter = _Tracker.CreateBloomFilter(FalsePositiveRate);
				Task.Factory.StartNew(() =>
				{
					node.SendMessage(new FilterLoadPayload(filter));
				});
			}
		}

		#region ICloneable Members

		public object Clone()
		{
			var clone = new TrackerBehavior(_Tracker, _ExplicitChain);
			clone.FalsePositiveRate = FalsePositiveRate;
			return clone;
		}

		#endregion


	}
}
#endif