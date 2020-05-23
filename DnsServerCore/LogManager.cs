﻿/*
Technitium DNS Server
Copyright (C) 2020  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using TechnitiumLibrary.IO;
using TechnitiumLibrary.Net.Dns;

namespace DnsServerCore
{
    public class LogManager : IDisposable
    {
        #region variables

        readonly string _logFolder;

        string _logFile;
        StreamWriter _logOut;
        DateTime _logDate;

        readonly ConcurrentQueue<LogQueueItem> _queue = new ConcurrentQueue<LogQueueItem>();
        readonly Thread _consumerThread;
        readonly object _logFileLock = new object();

        #endregion

        #region constructor

        public LogManager(string logFolder)
        {
            _logFolder = logFolder;

            StartNewLog();

            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Write((Exception)e.ExceptionObject);
            };

            //log consumer thread
            _consumerThread = new Thread(delegate ()
            {
                while (true)
                {
                    lock (_logFileLock)
                    {
                        if (_disposed)
                            break;

                        while (_queue.TryDequeue(out LogQueueItem item))
                        {
                            if (item._dateTime.Date > _logDate.Date)
                                StartNewLog();

                            WriteLog(item._dateTime, item._message);
                        }
                    }

                    Thread.Sleep(100);
                }
            });

            _consumerThread.IsBackground = true;
            _consumerThread.Start();
        }

        #endregion

        #region IDisposable

        bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            lock (_logFileLock)
            {
                if (_disposed)
                    return;

                if (disposing)
                {
                    if (_logOut != null)
                    {
                        WriteLog(DateTime.UtcNow, "Logging stopped.");
                        _logOut.Dispose();
                    }
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion

        #region private

        private void StartNewLog()
        {
            DateTime utcNow = DateTime.UtcNow;

            if ((_logOut != null) && (utcNow.Date > _logDate.Date))
            {
                WriteLog(utcNow, "Logging stopped.");
                _logOut.Close();
            }

            _logFile = Path.Combine(_logFolder, utcNow.ToString("yyyy-MM-dd") + ".log");
            _logOut = new StreamWriter(new FileStream(_logFile, FileMode.Append, FileAccess.Write, FileShare.Read));
            _logDate = utcNow;

            WriteLog(utcNow, "Logging started.");
        }

        private void WriteLog(DateTime dateTime, string message)
        {
            _logOut.WriteLine("[" + dateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC] " + message);
            _logOut.Flush();
        }

        #endregion

        #region static

        public static void DownloadLog(HttpListenerResponse response, string logFile, long limit)
        {
            using (FileStream fS = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                response.ContentType = "text/plain";
                response.AddHeader("Content-Disposition", "attachment;filename=" + Path.GetFileName(logFile));

                if ((limit > fS.Length) || (limit < 1))
                    limit = fS.Length;

                OffsetStream oFS = new OffsetStream(fS, 0, limit);

                using (Stream s = response.OutputStream)
                {
                    oFS.CopyTo(s);

                    if (fS.Length > limit)
                    {
                        byte[] buffer = System.Text.Encoding.UTF8.GetBytes("####___TRUNCATED___####");
                        s.Write(buffer, 0, buffer.Length);
                    }
                }
            }
        }

        #endregion

        #region public

        public void Write(Exception ex)
        {
            Write(ex.ToString());
        }

        public void Write(IPEndPoint ep, Exception ex)
        {
            Write(ep, ex.ToString());
        }

        public void Write(IPEndPoint ep, string message)
        {
            string ipInfo;

            if (ep == null)
                ipInfo = "";
            else if (ep.Address.IsIPv4MappedToIPv6)
                ipInfo = "[" + ep.Address.MapToIPv4().ToString() + ":" + ep.Port + "] ";
            else
                ipInfo = "[" + ep.ToString() + "] ";

            Write(ipInfo + message);
        }

        public void Write(IPEndPoint ep, DnsTransportProtocol protocol, Exception ex)
        {
            Write(ep, protocol, ex.ToString());
        }

        public void Write(IPEndPoint ep, DnsTransportProtocol protocol, DnsDatagram request, DnsDatagram response)
        {
            DnsQuestionRecord q = null;

            if (request.QDCOUNT > 0)
                q = request.Question[0];

            string question;

            if (q == null)
                question = "MISSING QUESTION!";
            else
                question = "QNAME: " + q.Name + "; QTYPE: " + q.Type.ToString() + "; QCLASS: " + q.Class;

            string responseInfo;

            if (response == null)
            {
                responseInfo = " NO RESPONSE FROM SERVER!";
            }
            else
            {
                string answer;

                if (response.ANCOUNT == 0)
                {
                    answer = "[]";
                }
                else
                {
                    answer = "[";

                    for (int i = 0; i < response.Answer.Count; i++)
                    {
                        if (i != 0)
                            answer += ", ";

                        answer += response.Answer[i].RDATA.ToString();
                    }

                    answer += "]";
                }

                responseInfo = " RCODE: " + response.RCODE.ToString() + "; ANSWER: " + answer;
            }

            Write(ep, protocol, question + ";" + responseInfo);
        }

        public void Write(IPEndPoint ep, DnsTransportProtocol protocol, string message)
        {
            string ipInfo;

            if (ep == null)
                ipInfo = "";
            else if (ep.Address.IsIPv4MappedToIPv6)
                ipInfo = "[" + ep.Address.MapToIPv4().ToString() + ":" + ep.Port + "] ";
            else
                ipInfo = "[" + ep.ToString() + "] ";

            Write(ipInfo + "[" + protocol.ToString().ToUpper() + "] " + message);
        }

        public void Write(string message)
        {
            _queue.Enqueue(new LogQueueItem(message));
        }

        public void DeleteCurrentLogFile()
        {
            lock (_logFileLock)
            {
                _logOut.Close();
                File.Delete(_logFile);

                StartNewLog();
            }
        }

        #endregion

        #region properties

        public string LogFolder
        { get { return _logFolder; } }

        public string CurrentLogFile
        { get { return _logFile; } }

        #endregion

        class LogQueueItem
        {
            #region variables

            public readonly DateTime _dateTime;
            public readonly string _message;

            #endregion

            #region constructor

            public LogQueueItem(string message)
            {
                _dateTime = DateTime.UtcNow;
                _message = message;
            }

            #endregion
        }
    }
}
