namespace Qoollo.Turbo
{
    /// <summary>
    /// Ƹ����� ������ �� ������ (������ GC)
    /// </summary>
    class StrongReferenceStorage : IWeakEventReferenceStorage
    {
        private object _target = null;

        /// <summary>
        /// ���������� ������
        /// </summary>
        public object Target
        {
            get
            {
                return _target;
            }
        }

        /// <summary>
        /// ����������� StrongReferenceStorage
        /// </summary>
        /// <param name="target">�������� ������</param>
        public StrongReferenceStorage(object target)
        {
            _target = target;
        }
    }
}