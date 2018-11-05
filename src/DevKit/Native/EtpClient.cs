﻿//----------------------------------------------------------------------- 
// ETP DevKit, 1.2
//
// Copyright 2018 Energistics
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//   
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Energistics.Etp.Common;
using Energistics.Etp.Common.Datatypes;
using Energistics.Etp.Properties;
using SuperSocket.ClientEngine;

namespace Energistics.Etp.Native
{
    /// <summary>
    /// An ETP client implemented using .NET websockets.
    /// </summary>
    /// <seealso cref="Energistics.Etp.Common.EtpSession" />
    public class EtpClient : EtpSessionNativeBase, IEtpClient
    {
        private static readonly IDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();
        private static readonly IDictionary<string, string> BinaryHeaders = new Dictionary<string, string>()
        {
            { Settings.Default.EtpEncodingHeader, Settings.Default.EtpEncodingBinary }
        };

        private string _supportedCompression;
        private Task _connectionHandlingTask;

        private CancellationTokenSource _source;
        private CancellationToken _token = CancellationToken.None;

        /// <summary>
        /// Initializes a new instance of the <see cref="EtpClient" /> class.
        /// </summary>
        /// <param name="uri">The ETP server URI.</param>
        /// <param name="application">The client application name.</param>
        /// <param name="version">The client application version.</param>
        /// <param name="etpSubProtocol">The ETP sub protocol.</param>
        public EtpClient(string uri, string application, string version, string etpSubProtocol) : this(uri, application, version, etpSubProtocol, EmptyHeaders)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EtpClient"/> class.
        /// </summary>
        /// <param name="uri">The ETP server URI.</param>
        /// <param name="application">The client application name.</param>
        /// <param name="version">The client application version.</param>
        /// <param name="etpSubProtocol">The ETP sub protocol.</param>
        /// <param name="headers">The WebSocket headers.</param>
        public EtpClient(string uri, string application, string version, string etpSubProtocol, IDictionary<string, string> headers)
            : base(new ClientWebSocket(), application, version, headers)
        {
            var headerItems = Headers.Union(BinaryHeaders.Where(x => !Headers.ContainsKey(x.Key))).ToList();

            ClientSocket.Options.AddSubProtocol(etpSubProtocol);
            foreach (var item in headerItems)
                ClientSocket.Options.SetRequestHeader(item.Key, item.Value);

            Uri = new Uri(uri);

            // TODO: Set the user agent.

            RegisterCoreClient(etpSubProtocol);
        }

        /// <summary>
        /// Cancellation token to use.
        /// </summary>
        protected override CancellationToken Token { get { return _token; } }

        /// <summary>
        /// The client websocket.
        /// </summary>
        private ClientWebSocket ClientSocket { get { return Socket as ClientWebSocket; } }

        /// <summary>
        /// The URI.
        /// </summary>
        private Uri Uri { get; set; }

        /// <summary>
        /// Opens the WebSocket connection.
        /// </summary>
        public void Open()
        {
            OpenAsync().Wait();
        }

        /// <summary>
        /// Asynchronously opens the WebSocket connection.
        /// </summary>
        public async Task<bool> OpenAsync()
        {
            if (IsOpen)
                return true;

            Logger.Trace(Log("Opening web socket connection..."));

            _source = new CancellationTokenSource();
            _token = _source.Token;

            try
            {
                await ClientSocket.ConnectAsync(Uri, Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (!IsOpen)
                return false;

            if (Token.IsCancellationRequested)
                return false;

            _connectionHandlingTask = Task.Factory.StartNew(async () => await HandleConnection(), Token, TaskCreationOptions.LongRunning, TaskScheduler.Current);

            Adapter.RequestSession(ApplicationName, ApplicationVersion, _supportedCompression);

            return true;
        }

        /// <summary>
        /// Asynchronously closes the WebSocket connection for the specified reason.
        /// </summary>
        /// <param name="reason">The reason.</param>
        protected override async Task CloseAsyncCore(string reason)
        {
            if (!IsOpen) return;

            _source?.Cancel();
            _token = CancellationToken.None;

            await base.CloseAsyncCore(reason);

            try
            {
                if (_connectionHandlingTask != null)
                {
                    await _connectionHandlingTask;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _connectionHandlingTask = null;
                _source?.Dispose();
            }
        }

        /// <summary>
        /// Sets the proxy server host name and port number.
        /// </summary>
        /// <param name="host">The host name.</param>
        /// <param name="port">The port number.</param>
        /// <param name="username">The username.</param>
        /// <param name="password">The password.</param>
        public void SetProxy(string host, int port, string username = null, string password = null)
        {
            if (Socket == null) return;

            var endPoint = new DnsEndPoint(host, port);
            var proxy = new WebProxy(endPoint.Host, endPoint.Port);
            
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                proxy.Credentials = new NetworkCredential(username, password);
            }

            // TODO: Handle using default credentials

            ClientSocket.Options.Proxy = proxy;
        }

        /// <summary>
        /// Sets the supported compression type, e.g. gzip.
        /// </summary>
        /// <param name="supportedCompression">The supported compression.</param>
        public void SetSupportedCompression(string supportedCompression)
        {
            _supportedCompression = supportedCompression;
        }

        /// <summary>
        /// Called to let derived classes cleanup after a connection has ended.
        /// </summary>
        protected override void CleanupAfterConnection()
        {
            Logger.Trace(Log("[{0}] Socket closed.", SessionId));
            SessionId = null;
        }

        /// <summary>
        /// Called when the ETP session is opened.
        /// </summary>
        /// <param name="requestedProtocols">The requested protocols.</param>
        /// <param name="supportedProtocols">The supported protocols.</param>
        public override void OnSessionOpened(IList<ISupportedProtocol> requestedProtocols, IList<ISupportedProtocol> supportedProtocols)
        {
            Logger.Trace(Log("[{0}] Socket opened.", SessionId));

            base.OnSessionOpened(requestedProtocols, supportedProtocols);
        }

        /// <summary>
        /// Handles the unsupported protocols.
        /// </summary>
        /// <param name="supportedProtocols">The supported protocols.</param>
        protected override void HandleUnsupportedProtocols(IList<ISupportedProtocol> supportedProtocols)
        {
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseAsync("Shutting down").Wait();

                _source = null;

                Socket?.Dispose();

                try
                {
                }
                catch
                {
                }

                _connectionHandlingTask = null;
            }

            base.Dispose(disposing);
        }
    }
}
