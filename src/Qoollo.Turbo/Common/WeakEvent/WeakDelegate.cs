using System;
using System.Runtime.CompilerServices;
using System.Diagnostics.Contracts;
using System.Reflection;

namespace Qoollo.Turbo
{
    /// <summary>
    /// ������ � ����� ��������� ��������
    /// </summary>
    public class WeakDelegate
    {
        /// <summary>
        /// ��������� ������
        /// </summary>
        private IWeakEventReferenceStorage _valueStorage;

        /// <summary>
        /// ��� ��������
        /// </summary>
        public Type DelegateType { get; private set; }

        /// <summary>
        /// ���������� �������. ����� ���� null
        /// </summary>
        public object Target
        {
            get
            {
                if (_valueStorage == null)
                    return null;
                return _valueStorage.Target;
            }
        }

        /// <summary>
        /// ����������� �� �������
        /// </summary>
        public bool IsStatic
        {
            get
            {
                return _valueStorage == null;
            }
        }

        /// <summary>
        /// ������� �� �������
        /// </summary>
        public bool IsActive
        {
            get
            {
                return _valueStorage == null || _valueStorage.Target != null;
            }
        }

        /// <summary>
        /// ���������� �����
        /// </summary>
        public MethodInfo Method { get; private set; }

        /// <summary>
        /// ������������ ��������
        /// </summary>
        /// <returns>�������</returns>
        public Delegate GetDelegate()
        {
            if (_valueStorage == null)
                return Delegate.CreateDelegate(DelegateType, Method, false);

            var target = _valueStorage.Target;
            if (target == null)
                return null;

            return Delegate.CreateDelegate(DelegateType, target, Method, false);
        }

        /// <summary>
        /// ����������� WeakDelegate
        /// </summary>
        /// <param name="value">�������, �� �������� ������</param>
        public WeakDelegate(Delegate value)
        {
            Contract.Requires(value != null);

            DelegateType = value.GetType();
            Method = value.Method;

            if (value.Target == null)
            {
                _valueStorage = null;
            }
            else if (Attribute.IsDefined(value.Method.DeclaringType, typeof(CompilerGeneratedAttribute)))
            {
                _valueStorage = new StrongReferenceStorage(value.Target);
            }
            else
            {
                _valueStorage = new WeakReferenceStorage(value.Target);
            }
        }
    }
}