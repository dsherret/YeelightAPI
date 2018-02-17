﻿using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using YeelightAPI.Models;

namespace YeelightAPI
{
    /// <summary>
    /// Yeelight Device
    /// </summary>
    public partial class Device : IDisposable
    {
        #region PRIVATE ATTRIBUTES

        /// <summary>
        /// Serializer settings
        /// </summary>
        private static readonly JsonSerializerSettings _serializerSettings = new JsonSerializerSettings()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        /// <summary>
        /// Dictionary of results
        /// </summary>
        private readonly Dictionary<int, ICommandResultHandler> _currentCommandResults = new Dictionary<int, ICommandResultHandler>();

        /// <summary>
        /// lock
        /// </summary>
        private readonly object _syncLock = new object();

        /// <summary>
        /// TCP client used to communicate with the device
        /// </summary>
        private TcpClient _tcpClient;

        #endregion PRIVATE ATTRIBUTES

        #region EVENTS

        /// <summary>
        /// Notification Received event
        /// </summary>
        public event NotificationReceivedEventHandler OnNotificationReceived;

        /// <summary>
        /// Error Received event handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public delegate void CommandErrorEventHandler(object sender, CommandErrorEventArgs e);

        /// <summary>
        /// Notification Received event handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public delegate void NotificationReceivedEventHandler(object sender, NotificationReceivedEventArgs e);

        #endregion EVENTS

        #region PUBLIC PROPERTIES

        /// <summary>
        /// HostName
        /// </summary>
        public string Hostname { get; }

        /// <summary>
        /// Port number
        /// </summary>
        public int Port { get; }

        #endregion PUBLIC PROPERTIES

        #region CONSTRUCTOR

        /// <summary>
        /// Constructor with a hostname and (optionally) a port number
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <param name="autoConnect"></param>
        public Device(string hostname, int port = Constantes.DefaultPort, bool autoConnect = false)
        {
            Hostname = hostname;
            Port = port;

            //autoconnect device if specified
            if (autoConnect)
            {
                Connect().Wait();
            }
        }

        internal Device(string hostname, int port, Dictionary<string, object> properties)
        {
            Hostname = hostname;
            Port = port;
            Properties = properties;
        }

        #endregion CONSTRUCTOR

        #region PROPERTIES ACCESS

        /// <summary>
        /// List of device properties
        /// </summary>
        public readonly Dictionary<string, object> Properties = new Dictionary<string, object>();

        /// <summary>
        /// Name of the device
        /// </summary>
        public string Name
        {
            get
            {
                return this[PROPERTIES.name] as string;
            }
            set
            {
                this[PROPERTIES.name] = value;
            }
        }

        /// <summary>
        /// Access property from its enum value
        /// </summary>
        /// <param name="property"></param>
        /// <returns></returns>
        public object this[PROPERTIES property]
        {
            get
            {
                return this[property.ToString()];
            }
            set
            {
                this[property.ToString()] = value;
            }
        }

        /// <summary>
        /// Access property from its name
        /// </summary>
        /// <param name="propertyName"></param>
        /// <returns></returns>
        public object this[string propertyName]
        {
            get
            {
                if (Properties.ContainsKey(propertyName))
                {
                    return Properties[propertyName];
                }
                return null;
            }
            set
            {
                if (Properties.ContainsKey(propertyName))
                {
                    Properties[propertyName] = value;
                }
                else if (!string.IsNullOrWhiteSpace(propertyName))
                {
                    Properties.Add(propertyName, value);
                }
            }
        }

        #endregion PROPERTIES ACCESS

        #region PUBLIC METHODS

        #region IDisposable

        /// <summary>
        /// Dispose the device
        /// </summary>
        public void Dispose()
        {
            Disconnect();
        }

        #endregion IDisposable

        /// <summary>
        /// Execute a command
        /// </summary>
        /// <param name="method"></param>
        /// <param name="id"></param>
        /// <param name="parameters"></param>
        /// <param name="smooth"></param>
        public void ExecuteCommand(METHODS method, int id = 0, List<object> parameters = null)
        {
            Command command = new Command()
            {
                Id = id,
                Method = method.GetRealName(),
                Params = parameters ?? new List<object>()
            };

            string data = JsonConvert.SerializeObject(command, _serializerSettings);
            byte[] sentData = Encoding.ASCII.GetBytes(data + Constantes.LineSeparator); // \r\n is the end of the message, it needs to be sent for the message to be read by the device

            lock (_syncLock)
            {
                _tcpClient.Client.Send(sentData);
            }
        }

        /// <summary>
        /// Execute a command and waits for a response
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="method"></param>
        /// <param name="id"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        public async Task<CommandResult<T>> ExecuteCommandWithResponse<T>(METHODS method, int id = 0, List<object> parameters = null)
        {
            try
            {
                return await UnsafeExecuteCommandWithResponse<T>(method, id, parameters);
            }
            catch (TaskCanceledException) { }

            return null;
        }

        #endregion PUBLIC METHODS

        #region PRIVATE METHODS

        /// <summary>
        /// Execute a command and waits for a response (Unsafe because of Task Cancelation)
        /// </summary>
        /// <param name="method"></param>
        /// <param name="id"></param>
        /// <param name="parameters"></param>
        /// <param name="smooth"></param>
        /// <exception cref="TaskCanceledException"></exception>
        /// <returns></returns>
        private Task<CommandResult<T>> UnsafeExecuteCommandWithResponse<T>(METHODS method, int id = 0, List<object> parameters = null)
        {
            CommandResultHandler<T> commandResultHandler;
            lock (_currentCommandResults)
            {
                if (_currentCommandResults.TryGetValue(id, out ICommandResultHandler oldHandler))
                {
                    oldHandler.TrySetCanceled();
                    _currentCommandResults.Remove(id);
                }

                commandResultHandler = new CommandResultHandler<T>();
                _currentCommandResults.Add(id, commandResultHandler);
            }

            ExecuteCommand(method, id, parameters);

            return commandResultHandler.Task;
        }

        /// <summary>
        /// Generate valid parameters for smooth values
        /// </summary>
        /// <param name="parameters"></param>
        /// <param name="smooth"></param>
        private static void HandleSmoothValue(ref List<object> parameters, int? smooth)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (smooth.HasValue)
            {
                parameters.Add("smooth");
                parameters.Add(smooth.Value);
            }
            else
            {
                parameters.Add("sudden");
                parameters.Add(null); // two parameters needed
            }
        }

        /// <summary>
        /// Watch for device responses and notifications
        /// </summary>
        /// <returns></returns>
        private async Task Watch()
        {
            await Task.Factory.StartNew(async () =>
            {
                //while device is connected
                while (_tcpClient != null)
                {
                    lock (_syncLock)
                    {
                        //there is data avaiblable in the pipe
                        if (_tcpClient.Client.Available > 0)
                        {
                            byte[] bytes = new byte[_tcpClient.Client.Available];

                            //read datas
                            _tcpClient.Client.Receive(bytes);

                            try
                            {
                                string datas = Encoding.UTF8.GetString(bytes);
                                if (!string.IsNullOrEmpty(datas))
                                {
                                    //get every messages in the pipe
                                    foreach (string entry in datas.Split(new string[] { Constantes.LineSeparator },
                                        StringSplitOptions.RemoveEmptyEntries))
                                    {
                                        CommandResult commandResult =
                                            JsonConvert.DeserializeObject<CommandResult>(entry, _serializerSettings);
                                        if (commandResult != null && commandResult.Id != 0)
                                        {
                                            ICommandResultHandler commandResultHandler;
                                            lock (_currentCommandResults)
                                            {
                                                commandResultHandler = _currentCommandResults[commandResult.Id];
                                            }

                                            if (commandResult.Error == null)
                                            {
                                                commandResult = (CommandResult)JsonConvert.DeserializeObject(entry, commandResultHandler.ResultType, _serializerSettings);
                                                commandResultHandler.SetResult(commandResult);
                                            }
                                            else
                                            {
                                                commandResultHandler.SetError(commandResult.Error);
                                            }
                                        }
                                        else
                                        {
                                            NotificationResult notificationResult =
                                                JsonConvert.DeserializeObject<NotificationResult>(entry,
                                                    _serializerSettings);

                                            if (notificationResult != null && notificationResult.Method != null)
                                            {
                                                if (notificationResult.Params != null)
                                                {
                                                    //save properties
                                                    foreach (KeyValuePair<PROPERTIES, object> property in
                                                        notificationResult.Params)
                                                    {
                                                        this[property.Key] = property.Value;
                                                    }
                                                }

                                                //notification result
                                                OnNotificationReceived?.Invoke(this,
                                                    new NotificationReceivedEventArgs(notificationResult));
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error while reading through pipe : {ex.Message}");
                            }
                        }
                    }

                    await Task.Delay(100);
                }
            }, TaskCreationOptions.LongRunning);
        }

        #endregion PRIVATE METHODS
    }
}