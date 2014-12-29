using System;

namespace Qoollo.Turbo
{
    /// <summary>
    /// ��������� ������ ������ �� ������ (�� ������������ ��� GC)
    /// </summary>
    class WeakReferenceStorage : IWeakEventReferenceStorage
    {
        private WeakReference _reference = null;

        /// <summary>
        /// ���������� ������. ����� ���� null.
        /// </summary>
        public object Target
        {
            get
            {
                return _reference.Target;
            }
        }

        /// <summary>
        /// ����������� WeakReferenceStorage
        /// </summary>
        /// <param name="reference">������</param>
        public WeakReferenceStorage(object reference)
        {
            _reference = new WeakReference(reference);
        }
    }
}