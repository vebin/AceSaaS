using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using Serilog;
using SuperSocket.SocketBase;
using SuperSocket.SocketBase.Protocol;
using Acesoft.Util;

namespace Acesoft.IotNet.Iot
{
	public class IotServer : AppServer<IotSession, IotRequest>, IDispatcher
	{
		private readonly ConcurrentDictionary<string, IotSession> sessions = new ConcurrentDictionary<string, IotSession>();
        private readonly ILogger logger;
		private int interval = 30;
		private IDispatcher api;

        public IotServer() : base(new DefaultReceiveFilterFactory<IotReceiveFilter, IotRequest>())
		{
			NewSessionConnected += IotServer_NewSessionConnected;
			NewRequestReceived += IotServer_NewRequestReceived;
			SessionClosed += IotServer_SessionClosed;
            logger = Log.ForContext<IotServer>();
        }

		private void IotServer_NewRequestReceived(IotSession session, IotRequest req)
		{
			logger.Debug($"IoT-Rece: {session.RemoteEndPoint} {req.Device.Mac}-{req.SessionId} {req.Command}");

			IotRequest request = null;
			var hasError = false;
			if (!req.CheckCrc16())
			{
				if (!req.Command.IsResponse)
				{
					request = req.ErrorCrc();
				}
				else
				{
					hasError = true;
				}
			}
			else
			{
				switch (req.Command.Name)
				{
				    case "00F1":
                    {
					    if (!req.CheckSession())
					    {
						    request = CreateErrorSession(req);
						    break;
					    }
					    if (!req.CheckValid())
					    {
						    request = req.ErrorDevice();
						    break;
					    }
					    request = req.Ok(interval.ToHex(2));
					    request.SessionId = RandomHelper.GetRandomHex(4);
					    session.SessionId = request.SessionId;
					    session.Device = req.Device;
					    sessions[req.Device.Mac] = session;
					    Task.Run(() => DoLogin(req));
					    break;
                    }
				    case "0001":
                    {
                        if (!req.CheckSession(sessions, session))
                        {
                            request = CreateErrorSession(req);
                            break;
                        }
                        Task.Run(() => DoUpload(req));
                        request = req.Ok();
                        break;
                    }
				    case "0003":
                    {
                        if (!req.CheckSession(sessions, session))
                        {
                            request = CreateErrorSession(req);
                            break;
                        }
                        Task.Run(() => DoOnline(req));
                        request = req.Ok(DatetimeHelper.GetNowHex());
                        break;
                    }
				    default:
                    {
                        if (req.Command.IsResponse)
                        {
                            if (!req.CheckSession(sessions, session))
                            {
                                hasError = true;
                            }
                            else
                            {
                                api.Dispatch(req.Device.Mac, "BACK", req.Command.Name, $"1{req.Command.DataHex}");
                            }
                        }
                        else if (!req.CheckSession(sessions, session))
                        {
                            request = CreateErrorSession(req);
                        }
                        else
                        {
                            Task.Run(() => DoUpData(req));
                            request = req.Ok();
                        }
                        break;
                    }
				}
			}

			if (!hasError && request != null)
			{
				Send(session, request);

                // success
			}
		}

		private IotRequest CreateErrorSession(IotRequest req)
		{
			if (sessions.ContainsKey(req.Device.Mac))
			{
				Task.Run(delegate
				{
					DoLogout(req.Device);
				});
			}
			return req.ErrorSession();
		}

		private void Send(IotSession session, IotRequest req)
		{
            logger.Debug($"IoT-Send: {session.RemoteEndPoint} {req.Device.Mac}-{req.SessionId} {req.Command}");

            var bytes = req.BuildBytes();
			session.Send(bytes, 0, bytes.Length);
		}

		public bool Dispatch(string mac, string action, string cmd, string data)
		{
			IotSession value = null;
			if (sessions.TryGetValue(mac, out value) && value.Connected)
			{
				var request = IotRequest.CreateRequest(mac, cmd, data);
				request.SessionId = value.SessionId;
				Send(value, request);
				return true;
			}

			api.Dispatch(mac, "BACK", cmd, "0设备已离线！");
			return false;
		}

		private void DoLogin(IotRequest req)
		{
			api.Dispatch(req.Device.Mac, "LOGIN", req.Command.Name, req.Command.DataHex);
		}

		private void DoOnline(IotRequest req)
		{
			api.Dispatch(req.Device.Mac, "ONLINE", req.Command.Name, "");
		}

		private void DoLogout(IotDevice device)
		{
			api.Dispatch(device.Mac, "LOGOUT", "", "");
		}

		private void DoUpload(IotRequest req)
		{
			api.Dispatch(req.Device.Mac, "UPLOAD", req.Command.Name, req.Command.DataHex);
		}

		private void DoUpData(IotRequest req)
		{
			api.Dispatch(req.Device.Mac, "UPDATA", req.Command.Name, req.Command.DataHex);
		}

		private void IotServer_NewSessionConnected(IotSession session)
		{
			logger.Debug($"IoT-Session-BGN: {session.RemoteEndPoint}");
		}

		private void IotServer_SessionClosed(IotSession session, CloseReason value)
		{
			if (session.Device != null)
			{
				DoLogout(session.Device);
            }

            logger.Debug($"IoT-Session-END: {session.RemoteEndPoint}");
        }

        protected override void OnStarted()
		{
			api = Bootstrap.GetServerByName("ApiServer") as IDispatcher;

			logger.Debug($"IoT-Socket-START: {Config.Ip}:{Config.Port}");
		}

		protected override void OnStopped()
		{
			logger.Debug($"IoT-Socket-STOP: {Config.Ip}:{Config.Port}");
		}
	}
}