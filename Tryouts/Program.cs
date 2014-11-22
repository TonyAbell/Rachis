﻿using System;
using System.Web.Http;
using Microsoft.Owin.Hosting;
using Owin;
using Rachis;
using Rachis.Tests;
using Rachis.Transport;
using Voron;

namespace Tryouts
{
	class Program
	{
		static void Main()
		{
			var _node1Transport = new HttpTransport("node1");
var node1 = new NodeConnectionInfo { Name = "node1", Uri = new Uri("http://localhost:9079") };
			var engineOptions = new RaftEngineOptions(node1, StorageEnvironmentOptions.CreateMemoryOnly(), _node1Transport , new DictionaryStateMachine())
			{
				MessageTimeout = 60 * 1000
			};
			var _raftEngine = new RaftEngine(engineOptions);

			var _server = WebApp.Start(new StartOptions
			{
				Urls = { "http://+:9079/" }
			}, builder =>
			{
				var httpConfiguration = new HttpConfiguration();
				RaftWebApiConfig.Load();
				httpConfiguration.MapHttpAttributeRoutes();
				httpConfiguration.Properties[typeof(HttpTransportBus)] = _node1Transport.Bus;
				builder.UseWebApi(httpConfiguration);
			});

			Console.ReadLine();
		}
	}

}
