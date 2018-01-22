using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.ServiceRuntime;
using Nest;
using TorControlClientNet;
using TorControlClientNet.Constants;
using TorControlClientNet.Entities;
using TorControlClientNet.Helper;

namespace ControlWorker
{
    public class WorkerRole : RoleEntryPoint
    {
        #region Fields

        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private readonly ManualResetEvent runCompleteEvent = new ManualResetEvent(false);
        private ElasticClient _elasticClient;
        private TorControlClient _torClient;

        #endregion

        #region Members

        public override void Run()
        {
            Trace.TraceInformation("ControlWorker is running");

            try
            {
                RunAsync(cancellationTokenSource.Token).Wait();
            }
            finally
            {
                runCompleteEvent.Set();
            }
        }

        public override bool OnStart()
        {
            // Set the maximum number of concurrent connections
            ServicePointManager.DefaultConnectionLimit = 12;

            var result = base.OnStart();

            var settings = new ConnectionSettings(new Uri("http://localhost:9200")).DefaultIndex("tor");

            _elasticClient = new ElasticClient(settings);
            _elasticClient.DeleteIndex("tor");

            _torClient = new TorControlClient("127.0.0.1", 9051);

            _torClient.OnSuccessfullAuthentication += TorClient_OnSuccessfullAuthentication;
            _torClient.OnBadAuthentication += TorClient_OnBadAuthentication;
            _torClient.OnAsyncEvent += TorClient_OnAsyncEvent;
            _torClient.OnCommandData += _torClient_OnCommandData;
            _torClient.Connect();

            _torClient.Authenticate();

            Trace.TraceInformation("ControlWorker has been started");

            return result;
        }

        private void _torClient_OnCommandData(object sender, EventArgs e)
        {
            LogCommand(e);
        }

        private void LogCommand(EventArgs e)
        {
            var args = e as TorEventArgs;

            foreach (var value in args.Values)
            {
                var torControlLog = new TorControlLog();
                torControlLog.EventName = args.EventName;
                torControlLog.Value = value;
                if (double.TryParse(value, out var result))
                    torControlLog.ValueNum = result / 1000000;

                _elasticClient.Index(torControlLog);
            }
        }

        private void TorClient_OnAsyncEvent(object sender, EventArgs e)
        {
            var args = e as TorEventArgs;
            var values = args.EventName.Split(' ');
            if (values[1] == "BW")
            {
                var torControlLog = new TorControlLog();
                torControlLog.EventName = "BytesRead";
                torControlLog.Value = values[2];
                if (double.TryParse(values[2], out var result))
                    torControlLog.ValueNum = result / 1000000;

                _elasticClient.Index(torControlLog);

                torControlLog = new TorControlLog();
                torControlLog.EventName = "BytesWritten";
                torControlLog.Value = values[2];
                if (double.TryParse(values[2], out var result2))
                    torControlLog.ValueNum = result2 / 1000000;

                _elasticClient.Index(torControlLog);
            }
        }

        private void TorClient_OnBadAuthentication(object sender, EventArgs e)
        {
        }

        private void TorClient_OnSuccessfullAuthentication(object sender, EventArgs e)
        {
        }


        public override void OnStop()
        {
            Trace.TraceInformation("ControlWorker is stopping");

            cancellationTokenSource.Cancel();
            runCompleteEvent.WaitOne();

            base.OnStop();

            Trace.TraceInformation("ControlWorker has stopped");
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            _torClient.SendCommand(TorCommands.SETEVENTS, "BW");

            // TODO: Replace the following with your own logic.
            while (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceInformation("Working");
                if (_torClient.IsAuthenticated)
                {
                    _torClient.SendCommand(TorCommands.GETINFO, TorGetInfoKeywords.trafficread.GetStringValue());
                    _torClient.SendCommand(TorCommands.GETINFO, TorGetInfoKeywords.trafficwritten.GetStringValue());
                }

                await Task.Delay(10000);
            }
        }

        #endregion
    }
}