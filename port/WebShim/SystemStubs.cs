// Stand-ins for class library types that Unity's WebGL profile of System.dll leaves out but mods use.
// The patcher retargets references to them from System to WebShim (StubbedSystemTypes), so names and
// signatures must match the .NET ones. Unlike UnityStubs these are real implementations.
//
// Mod the Gungeon API's SGUI keeps its element trees in BindingLists and listens to ListChanged.
#pragma warning disable 0436

using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace System.ComponentModel
{
    public class AddingNewEventArgs : EventArgs
    {
        public AddingNewEventArgs()
        {
        }

        public AddingNewEventArgs(object newObject)
        {
            NewObject = newObject;
        }

        public object NewObject { get; set; }
    }

    public delegate void AddingNewEventHandler(object sender, AddingNewEventArgs e);

    /// <summary>Collection&lt;T&gt; raising ListChanged like System.ComponentModel.BindingList&lt;T&gt;.</summary>
    public class BindingList<T> : Collection<T>
    {
        public BindingList()
        {
            RaiseListChangedEvents = true;
        }

        public BindingList(IList<T> list)
            : base(list)
        {
            RaiseListChangedEvents = true;
        }

        public event ListChangedEventHandler ListChanged;

        public event AddingNewEventHandler AddingNew;

        public bool RaiseListChangedEvents { get; set; }

        public bool AllowNew { get; set; }

        public bool AllowEdit { get { return true; } set { } }

        public bool AllowRemove { get { return true; } set { } }

        public T AddNew()
        {
            var e = new AddingNewEventArgs();
            AddingNewEventHandler handler = AddingNew;
            if (handler != null)
            {
                handler(this, e);
            }
            T item = e.NewObject is T ? (T)e.NewObject : Activator.CreateInstance<T>();
            Add(item);
            return item;
        }

        public void ResetBindings()
        {
            OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
        }

        public void ResetItem(int position)
        {
            OnListChanged(new ListChangedEventArgs(ListChangedType.ItemChanged, position));
        }

        protected virtual void OnListChanged(ListChangedEventArgs e)
        {
            ListChangedEventHandler handler = ListChanged;
            if (RaiseListChangedEvents && handler != null)
            {
                handler(this, e);
            }
        }

        protected override void ClearItems()
        {
            base.ClearItems();
            OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
        }

        protected override void InsertItem(int index, T item)
        {
            base.InsertItem(index, item);
            OnListChanged(new ListChangedEventArgs(ListChangedType.ItemAdded, index));
        }

        protected override void RemoveItem(int index)
        {
            base.RemoveItem(index);
            OnListChanged(new ListChangedEventArgs(ListChangedType.ItemDeleted, index));
        }

        protected override void SetItem(int index, T item)
        {
            base.SetItem(index, item);
            OnListChanged(new ListChangedEventArgs(ListChangedType.ItemChanged, index));
        }
    }
}
