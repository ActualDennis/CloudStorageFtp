﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CloudStorage.Server.Authentication;
using CloudStorage.Server.Connections;
using CloudStorage.Server.Data;
using CloudStorage.Server.Di;
using CloudStorage.Server.Factories;
using CloudStorage.Server.FileSystem;
using CloudStorage.Server.Helpers;
using CloudStorage.Server.Logging;
using CloudStorage.Server.Misc;

namespace CloudStorage.Server {

    public class FtpServer : IDisposable
    {
        public FtpServer(
            ILogger logger)
        {
            this.logger = logger;
        }

        private TcpListener ConnectionsListener { get; set; }

        private Dictionary<Task, CancellationTokenSource> connections { get; set; } = new Dictionary<Task, CancellationTokenSource>();

        private ILogger logger { get; set; }

        public void Initialize()
        {
            try
            {
                var settings = XmlConfigParser.ParseSettings();

                //For server to operate as you want , 
                //you must set desired values in Configuration.xml
                //If you are using ssl certificate, it should be without password for now.
                DefaultServerValues.CertificateLocation = settings.CertificateLocation;
                DefaultServerValues.BaseDirectory = settings.BaseDirectory;
                DefaultServerValues.FtpControlPort = settings.FtpControlPort;
                DefaultServerValues.ServerExternalIP = settings.ServerExternalIP;
                DefaultServerValues.LoggingPath = settings.LoggingPath;
                DataConnection.MaxPort = settings.MaxPort;
                DataConnection.MinPort = settings.MinPort;
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Server did not start properly. Fix these errors and try again: {ex.Message}");
            }
        }

        public void Dispose()
        {
            ConnectionsListener.Stop();
            connections = null;
        }
        /// <summary>
        /// Call this method if you want to start listening for incoming requests
        /// and allow users to manage their virtual storage
        /// </summary>
        /// <returns></returns>
        public async Task Start(int Port, bool IsEncryptionEnabled)
        {
            DiContainer.ValidateProvider();
            ConnectionsListener = new TcpListener(IPAddress.Any, Port);
            ConnectionsListener.Start();
            while (true)
                try
                {
                    var connectedClient = ConnectionsListener.AcceptTcpClient();

                    ActionsTracker.UserConnected(null, connectedClient.Client.RemoteEndPoint);

                    var controlConnection = DiContainer.Provider.Resolve<ControlConnection>();
                    controlConnection.Initialize(connectedClient, IsEncryptionEnabled);
                    
                    var cts = new CancellationTokenSource();
                    cts.Token.Register(() => controlConnection.Dispose());
                    connections.Add(Task.Run(() => controlConnection.InitiateConnection()), cts);
                }
                catch (DirectoryNotFoundException)
                {
                    logger.Log("Wrong server base directory was provided.", RecordKind.Error);
                    Dispose();
                    break;
                }
                catch (InvalidOperationException) { return; }
                catch (SocketException)
                {
                    logger.Log("Stopped the server.", RecordKind.Status);
                }
        }

        /// <summary>
        /// Cancels and removes all tasks in <see cref="connections"/> if flag is true,
        /// Waits for tasks to complete / removes all tasks if flag is false.
        /// </summary>
        /// <param name="waitForUsersToDisconnect"></param>
        /// <returns></returns>
        public async Task Stop(bool waitForUsersToDisconnect)
        {
            try
            {
                if (!waitForUsersToDisconnect)
                {
                    foreach (var task in connections)
                    {
                        if (task.Key.IsCanceled || task.Key.IsCompleted)
                            continue;

                        connections[task.Key].Cancel();
                        connections[task.Key].Dispose();
                    }

                    return;
                }

                logger.Log("Waiting for all users to disconnect.", RecordKind.Status);

                Task.WaitAll(connections.Keys.ToArray());
            }
            finally
            {
                Dispose();
                logger.Log("Stopped the server.", RecordKind.Status);
            }

        }
    }
}
