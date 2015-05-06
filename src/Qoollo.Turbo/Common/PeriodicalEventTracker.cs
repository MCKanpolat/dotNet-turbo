using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.Contracts;

namespace Qoollo.Turbo
{
    /// <summary>
    /// ������������ ������� ��� ���������� �������� � ���������� ����������� ��������.
    /// �����������, ���� ������� ��������� �����, � �������� ����� ������������� � �������� ���������� �������.
    /// ������: ����������� ����������
    /// </summary>
    public class PeriodicalEventTracker
    {
        /// <summary>
        /// ������� ��� ���������� �������� ��� ����������� �������, ����� ��������� ������ ������� ����
        /// </summary>
        /// <param name="isFirstTime">������ �� ��������� �������</param>
        public delegate void ActionOnPeriodPassed(bool isFirstTime);
        
        // =========
        
        /// <summary>
        /// ��������� ��������� ������� � �������������
        /// </summary>
        /// <returns>��������� �����</returns>
        private static int GetTimeStamp()
        {
            int result = Environment.TickCount;
            return result != 0 ? result : 1;
        }
        
        // ============
        
		private readonly int _periodMs;
        private int _registeredTimeStamp;

        /// <summary>
        /// ����������� PeriodicalEventTracker. 
        /// ������ �� ���������: 5 �����.
        /// </summary>
        public PeriodicalEventTracker()
            : this(5 * 60 * 1000)
        {
        }
        /// <summary>
        /// ����������� PeriodicalEventTracker
        /// </summary>
        /// <param name="period">������ ������������ �� �������</param>
        public PeriodicalEventTracker(TimeSpan period)
            : this((int)period.TotalMilliseconds)
        {
			Contract.Requires<ArgumentException>(period >= TimeSpan.Zero);
            Contract.Requires<ArgumentException>(period.TotalMilliseconds < int.MaxValue);
        }
        /// <summary>
        /// ����������� PeriodicalEventTracker
        /// </summary>
        /// <param name="periodMs">������ ������������ �� �������</param>
        public PeriodicalEventTracker(int periodMs)
        {
            Contract.Requires<ArgumentException>(periodMs >= 0);
            
            _periodMs = periodMs;
            _registeredTimeStamp = 0;
        }

        /// <summary>
        /// ���������������� �� ��� �������
        /// </summary>
        public bool IsEventRegistered { get { return Volatile.Read(ref _registeredTimeStamp) != 0; } }
        /// <summary>
        /// ������ �� ������, ����� ������� ����� ������������� �� �������
        /// </summary>
		public bool IsPeriodPassed 
		{
			get
			{
				int registeredTimeStamp = Volatile.Read(ref _registeredTimeStamp);
				return registeredTimeStamp == 0 || GetTimeStamp() - registeredTimeStamp > _periodMs;
			}
		}

        /// <summary>
        /// ���������������� ������� � �������� ���������� � ������������� �������
        /// </summary>
        /// <param name="firstTime">������ �� ��������� �������</param>
        /// <returns>����� �� ����������� �� ��� �������</returns>
        public bool Register(out bool firstTime)
        {
            int newTimeStamp = GetTimeStamp();
            int registeredTimeStamp = Volatile.Read(ref _registeredTimeStamp);
            firstTime = registeredTimeStamp == 0;

            if (registeredTimeStamp == 0 || newTimeStamp - registeredTimeStamp > _periodMs)
            {
                Interlocked.Exchange(ref _registeredTimeStamp, newTimeStamp);
                return true;
            }

            return false;
        }
        /// <summary>
        /// ���������������� ������� � �������� ���������� � ������������� �������
        /// </summary>
        /// <returns>����� �� ����������� �� ��� �������</returns>
        public bool Register()
        {
            int newTimeStamp = GetTimeStamp();
            int registeredTimeStamp = Volatile.Read(ref _registeredTimeStamp);

            if (registeredTimeStamp == 0 || newTimeStamp - registeredTimeStamp > _periodMs)
            {
                Interlocked.Exchange(ref _registeredTimeStamp, newTimeStamp);
                return true;
            }

            return false;
        }
		/// <summary>
        /// ���������������� ������� � ������� ������� ��� ������������� ���������
		/// </summary>
		/// <param name="action">�������� �� ������� �� �������</param>
		public void Register(ActionOnPeriodPassed action)
		{
			if (action == null)
                throw new ArgumentNullException("action");
                
            int newTimeStamp = GetTimeStamp();
            int registeredTimeStamp = Volatile.Read(ref _registeredTimeStamp);

            if (registeredTimeStamp == 0 || newTimeStamp - registeredTimeStamp > _periodMs)
            {
                Interlocked.Exchange(ref _registeredTimeStamp, newTimeStamp);
                action(registeredTimeStamp == 0);
            }
		}

        /// <summary>
        /// �������� ���� ������� �������
        /// </summary>
        public void Reset()
        {
            if (Volatile.Read(ref _registeredTimeStamp) != 0)
                Interlocked.Exchange(ref _registeredTimeStamp, 0);
        }
    }
}