using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;

namespace ProcessCaddy
{ 
	public delegate void OnEventFn( string evt );
	public class ProcessManager : IProcessManager
	{
		Database m_database = new Database();
		HeartbeatMonitor m_heartbeatMonitor;
		List<INotificationReceiver> m_notificationReceivers = new List<INotificationReceiver>();
		OnEventFn m_onEvent;
		public class ProcessEntry
		{
			public Process process;
			public ProcessStartInfo pinfo;
			public string name;
			public string exec;
			public string args;
            public Database.ScheduleInfo sched;
			public bool restartOnExit;
		}

		public enum Status
		{
			Idle = 0,
			Running = 1
		}

		List<ProcessEntry> m_processList = new List<ProcessEntry>();
        int m_curProcessIndex;
		public ProcessManager()
		{
			m_heartbeatMonitor = new HeartbeatMonitor(this);
		}

		public void AddListener(OnEventFn fn)
		{
			m_onEvent += fn;
		}

		public Status GetProcessStatus(int index)
		{
			if (index < 0 || index >= m_processList.Count)
			{
				return Status.Idle;
			}

			if (m_processList[index].process == null)
			{
				return Status.Idle;
			}

			if (m_processList[index].process.HasExited)
			{
				return Status.Idle;
			}

			return Status.Running;
		}

        public int GetStartTime(int index)
        {
            if (index < 0 || index >= m_processList.Count)
            {
                return -1;
            }

            return m_processList[index].sched.startTime;
        }
		public bool Init()
		{
			if (!m_database.Load("config.json"))
			{
				return false;
			}

			m_processList.Clear();

			foreach (Database.Entry entry in m_database.Entries)
			{
				AddProcess(entry.name, entry.exec, entry.args, entry.sched);
			}

			m_onEvent?.Invoke("ConfigLoaded");

			return true;
		}

		public int Count
		{
			get { return m_processList.Count; }
		}

		public void Update()
		{
            m_heartbeatMonitor.Update();
		}

		public Database.Entry GetEntryAtIndex(int index)
		{
			if (index < 0 || index >= m_database.Entries.Count)
			{
				throw new ArgumentOutOfRangeException();
			}

			return m_database.Entries[index];
		}

		public int AddProcess(string name, string exec, string args, Database.ScheduleInfo sched)
		{
			ProcessEntry entry = new ProcessEntry();

			entry.name = name;
			entry.exec = exec;
			entry.args = args;
            entry.sched = sched;

			m_processList.Add(entry);

			return m_processList.Count - 1; //this is the index now assigned to the process.
		}

		void OnProcessExit(object sender, EventArgs e)
		{
			Process proc = (Process)sender;
			Console.WriteLine("Process exited: " + proc.Id);

			for (int i = 0; i < m_processList.Count; i++)
			{
				if (m_processList[i].process == proc)
				{
					m_processList[i].process = null;

					OnProcessExited( m_processList[i] );

					if (m_processList[i].restartOnExit)
					{
						StartInternal(m_processList[i]);
					}
				}
			}

			m_onEvent?.Invoke("StatusUpdated");


		}

		public bool Start(int index)
		{
			ProcessEntry entry = m_processList[index];

			if (entry.process != null)
			{
				if (!entry.process.HasExited)
				{
					Console.WriteLine("Error: process already started");
					return false;
				}
			}

			entry.pinfo = new ProcessStartInfo(entry.exec);
			string workingDirectory = Path.GetDirectoryName(entry.exec);
			entry.pinfo.WorkingDirectory = workingDirectory;
			entry.pinfo.Arguments = entry.args;

			return StartInternal(entry);
		}

		[DllImport("user32.dll")] static extern bool SetWindowText(IntPtr hWnd, string text);
		private bool StartInternal(ProcessEntry entry)
		{
			try
			{
				entry.process = Process.Start(entry.pinfo);
				entry.process.EnableRaisingEvents = true;
				entry.process.Exited += OnProcessExit;
				entry.restartOnExit = true;

				if (entry.name.Length > 0)
				{
					//entry.process.WaitForInputIdle(500);
					Thread.Sleep(500);
					SetWindowText(entry.process.MainWindowHandle, entry.name);
				}

				OnProcessStarted( entry );

				m_onEvent?.Invoke("StatusUpdated");
				return true;
			} catch (System.Exception)
			{
				m_onEvent?.Invoke("StartFailure");
			}

			return false;
		}

		public bool Stop(int index)
		{
			ProcessEntry entry = m_processList[index];

			if (entry.process == null || entry.process.HasExited)
			{
				Console.WriteLine("Error: process already exited");
				return false;
			}

			try
			{
				entry.restartOnExit = false;
				entry.process.Kill();
				OnProcessStopped( entry );
				return true;
			}
			catch (System.Exception)
			{
				//TODO: Display error dialog
				m_onEvent?.Invoke("StopFailure");
			}

			return false;
		}

		public bool Restart(int index)
		{
			ProcessEntry entry = m_processList[index];

			if (entry.process == null || entry.process.HasExited)
			{
				Console.WriteLine("Error: process already exited");
				return true;
			}

			try
			{
				entry.restartOnExit = true;
				entry.process.Kill();
				OnProcessStopped( entry );
				return true;
			}
			catch (System.Exception)
			{
				//TODO: Display error dialog
				m_onEvent?.Invoke("StopFailure");
			}

			return false;
		}

		public void StartAll()
		{
            //TESTCODE

            // find the process that should be starting now
            int currentTime = GetCurTime();

            for(int i = 0; i < m_processList.Count; i++)
            {
                if((m_processList[i].sched.startTime < currentTime) && (m_processList[i].sched.endTime > currentTime))
                {
                    Start(i);
                    m_curProcessIndex = i;
                    break;
                }
            }

			//for (int i = 0; i < m_processList.Count; i++)
			//{
			//	Start(i);
			//}
		}

		public void StopAll()
		{
			for (int i = 0; i < m_processList.Count; i++)
			{
				Stop(i);
			}
		}

		#region IProcessManager
		public bool FindProcessById(int pid)
		{
			foreach (ProcessEntry proc in m_processList)
			{
				if (proc != null && proc.process != null && proc.process.Id == pid)
				{
					return true;
				}
			}

			return false;
		}

		public void RestartProcessById(int pid)
		{
			for (int i = 0; i < m_processList.Count; i++)
			{
				if (pid == m_processList[i].process.Id)
				{
					Restart(i);
					return;
				}
			}
		}

		public void RegisterForNotifications( INotificationReceiver receiver )
		{
			if ( !m_notificationReceivers.Contains( receiver ) )
			{
				m_notificationReceivers.Add( receiver );
			}
		}
		public void UnregisterFromNotifications( INotificationReceiver receiver )
		{
			if ( m_notificationReceivers.Contains( receiver ) )
			{
				m_notificationReceivers.Remove( receiver );
			}
		}

        public void CheckSchedule()
        {
            int currentTime = GetCurTime();

            // reload the config file to check for changed schedules
            if (m_database.Load("config.json"))
            {
                foreach (Database.Entry entry in m_database.Entries)
                {
                    foreach (ProcessEntry proc in m_processList)
                    {
                        if(proc.name == entry.name)
                        {
                            proc.sched = entry.sched;
                        }
                    }
                }
            }

            if ((m_processList[m_curProcessIndex].sched.startTime > currentTime) || (m_processList[m_curProcessIndex].sched.endTime < currentTime))
            {
                Stop(m_curProcessIndex);

                for (int i = 0; i < m_processList.Count; i++)
                {
                    if ((m_processList[i].sched.startTime < currentTime) && (m_processList[i].sched.endTime > currentTime))
                    {
                        Start(i);
                        m_curProcessIndex = i;
                        break;
                    }
                }
            }

            m_onEvent?.Invoke("StatusUpdated");
        }
		#endregion

		void OnProcessStarted(ProcessManager.ProcessEntry entry)
		{
			foreach( INotificationReceiver recv in m_notificationReceivers )
			{
				recv.OnProcessStarted( entry );
			}
		}
		void OnProcessStopped(ProcessManager.ProcessEntry entry)
		{
			foreach ( INotificationReceiver recv in m_notificationReceivers)
			{
				recv.OnProcessStopped(entry);
			}
		}
		void OnProcessExited(ProcessManager.ProcessEntry entry)
		{
			foreach ( INotificationReceiver recv in m_notificationReceivers)
			{
				recv.OnProcessExited(entry);
			}
		}
		void OnProcessRestarted(ProcessManager.ProcessEntry entry)
		{
			foreach ( INotificationReceiver recv in m_notificationReceivers)
			{
				recv.OnProcessRestarted(entry);
			}
		}

        int GetCurTime()
        {
            return System.DateTime.Now.Second;
        }
	}

}