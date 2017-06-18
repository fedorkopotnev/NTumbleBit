﻿using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NBitcoin.RPC;
using NTumbleBit.ClassicTumbler;
using NTumbleBit.Client.Tumbler;
using NTumbleBit.TumblerServer;
using NTumbleBit.TumblerServer.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NTumbleBit.Tests
{
	public class TumblerServerTester : IDisposable
	{
		public static TumblerServerTester Create([CallerMemberNameAttribute]string caller = null)
		{
			return new TumblerServerTester(caller);
		}
		public TumblerServerTester(string directory)
		{
			try
			{

				var rootTestData = "TestData";
				directory = rootTestData + "/" + directory;
				_Directory = directory;
				if(!Directory.Exists(rootTestData))
					Directory.CreateDirectory(rootTestData);

				if(!TryDelete(directory, false))
				{
					foreach(var process in Process.GetProcessesByName("bitcoind"))
					{
						if(process.MainModule.FileName.Replace("\\", "/").StartsWith(Path.GetFullPath(rootTestData).Replace("\\", "/"), StringComparison.Ordinal))
						{
							process.Kill();
							process.WaitForExit();
						}
					}
					TryDelete(directory, true);
				}

				_NodeBuilder = NodeBuilder.Create(directory);
				_TumblerNode = _NodeBuilder.CreateNode(false);
				_AliceNode = _NodeBuilder.CreateNode(false);
				_BobNode = _NodeBuilder.CreateNode(false);

				Directory.CreateDirectory(directory);

				_NodeBuilder.StartAll();

				SyncNodes();
				
				var conf = new TumblerConfiguration();
				conf.DataDir = Path.Combine(directory, "server");
				Directory.CreateDirectory(conf.DataDir);
				File.WriteAllBytes(Path.Combine(conf.DataDir, "Tumbler.pem"), TestKeys.Default.ToBytes());
				File.WriteAllBytes(Path.Combine(conf.DataDir, "Voucher.pem"), TestKeys.Default2.ToBytes());
				conf.RPC.Url = TumblerNode.CreateRPCClient().Address;
				var creds = ExtractCredentials(File.ReadAllText(_TumblerNode.Config));
				conf.RPC.User = creds.Item1;
				conf.RPC.Password = creds.Item2;
				conf.Network = Network.RegTest;
				conf.ClassicTumblerParameters.FakePuzzleCount /= 4;
				conf.ClassicTumblerParameters.FakeTransactionCount /= 4;
				conf.ClassicTumblerParameters.RealTransactionCount /= 4;
				conf.ClassicTumblerParameters.RealPuzzleCount /= 4;
				conf.ClassicTumblerParameters.CycleGenerator.FirstCycle.Start = 105;

				var runtime = TumblerRuntime.FromConfiguration(conf);
				_Host = new WebHostBuilder()
					.UseKestrel()
					.UseAppConfiguration(runtime)
					.UseContentRoot(Path.GetFullPath(directory))
					.UseIISIntegration()
					.UseStartup<Startup>()
					.Build();

				_Host.Start();
				ServerRuntime = runtime;

				//Overrides server fee
				((TumblerServer.Services.RPCServices.RPCFeeService)runtime.Services.FeeService).FallBackFeeRate = new FeeRate(Money.Satoshis(100), 1);


				var clientConfig = new TumblerClientConfiguration();
				clientConfig.DataDir = Path.Combine(directory, "client");
				Directory.CreateDirectory(clientConfig.DataDir);
				clientConfig.Network = conf.Network;
				clientConfig.OutputWallet.KeyPath = new KeyPath("0");
				clientConfig.OutputWallet.RootKey = new ExtKey().Neuter().GetWif(conf.Network);
				clientConfig.RPCArgs.Url = AliceNode.CreateRPCClient().Address;
				creds = ExtractCredentials(File.ReadAllText(AliceNode.Config));
				clientConfig.RPCArgs.User = creds.Item1;
				clientConfig.RPCArgs.Password = creds.Item2;
				clientConfig.TumblerServer = Address;

				ClassicTumblerParameters p;
				ClientRuntime = TumblerClientRuntime.FromConfiguration(clientConfig, out p);
				if(p == null)
					throw new Exception("Client should confirm tumbler params");
				ClientRuntime.Confirm(p);

				//Overrides client fee
				((Client.Tumbler.Services.RPCServices.RPCFeeService)ClientRuntime.Services.FeeService).FallBackFeeRate = new FeeRate(Money.Satoshis(50), 1);
			}
			catch { Dispose(); throw; }
		}

		public PaymentStateMachine CreateStateMachine()
		{
			return ClientRuntime.CreateStateMachineJob().CreateStateMachine(null);
		}

		private Tuple<string, string> ExtractCredentials(string config)
		{
			var user = Regex.Match(config, "rpcuser=([^\r\n]*)");
			var pass = Regex.Match(config, "rpcpassword=([^\r\n]*)");
			return Tuple.Create(user.Groups[1].Value, pass.Groups[1].Value);
		}

		public TumblerRuntime ServerRuntime
		{
			get; set;
		}

		public void MineTo(CoreNode node, CycleParameters cycle, CyclePhase phase, bool end = false, int offset = 0)
		{
			var height = node.CreateRPCClient().GetBlockCount();
			var periodStart = end ? cycle.GetPeriods().GetPeriod(phase).End : cycle.GetPeriods().GetPeriod(phase).Start;
			var blocksToFind = periodStart - height + offset;
			if(blocksToFind <= 0)
				return;

			node.FindBlock(blocksToFind);
			SyncNodes();
		}

		public void RefreshWalletCache()
		{
			if(ClientRuntime != null)
				ClientRuntime.Services.BlockExplorerService.WaitBlock(uint256.Zero, default(CancellationToken));
			if(ServerRuntime != null)
				ServerRuntime.Services.BlockExplorerService.WaitBlock(uint256.Zero, default(CancellationToken));
		}

		public TumblerClientRuntime ClientRuntime
		{
			get; set;
		}

		public void SyncNodes()
		{
			foreach(var node in NodeBuilder.Nodes)
			{
				foreach(var node2 in NodeBuilder.Nodes)
				{
					if(node != node2)
						node.Sync(node2, true);
				}
			}
			RefreshWalletCache();
		}

		private static bool TryDelete(string directory, bool throws)
		{
			try
			{
				Utils.DeleteRecursivelyWithMagicDust(directory);
				return true;
			}
			catch(DirectoryNotFoundException)
			{
				return true;
			}
			catch(Exception)
			{
				if(throws)
					throw;
			}
			return false;
		}

		private readonly CoreNode _TumblerNode;
		public CoreNode TumblerNode
		{
			get
			{
				return _TumblerNode;
			}
		}

		private NodeBuilder _NodeBuilder;
		public NodeBuilder NodeBuilder
		{
			get
			{
				return _NodeBuilder;
			}
		}

		private IWebHost _Host;
		public IWebHost Host
		{
			get
			{
				return _Host;
			}
		}

		public Uri Address
		{
			get
			{

				var address = ((KestrelServer)(_Host.Services.GetService(typeof(IServer)))).Features.Get<IServerAddressesFeature>().Addresses.FirstOrDefault();
				return new Uri(address);
			}
		}

		public TumblerClient CreateTumblerClient()
		{
			return new TumblerClient(ServerRuntime.Network, Address);
		}

		private readonly string _Directory;
		private readonly CoreNode _AliceNode;
		public CoreNode AliceNode
		{
			get
			{
				return _AliceNode;
			}
		}


		private readonly CoreNode _BobNode;
		public CoreNode BobNode
		{
			get
			{
				return _BobNode;
			}
		}

		public string BaseDirectory
		{
			get
			{
				return _Directory;
			}
		}

		public void Dispose()
		{
			if(_Host != null)
			{

				_Host.Dispose();
				_Host = null;
			}
			if(_NodeBuilder != null)
			{
				_NodeBuilder.Dispose();
				_NodeBuilder = null;
			}
			if(ClientRuntime != null)
			{
				ClientRuntime.Dispose();
				ClientRuntime = null;
			}
			if(ServerRuntime != null)
			{
				ServerRuntime.Dispose();
				ServerRuntime = null;
			}
		}
	}
}
