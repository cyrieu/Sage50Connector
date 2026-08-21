using Sage.Peachtree.API;
using System;
using System.Collections.Generic;

namespace Sage50Connector.Helpers
{
    /// <summary>
    /// Maps Sage entity keys to the ID strings Rutter stores as platform_id.
    ///
    /// Why this exists: a transaction points at its account, customer and vendor
    /// through an <see cref="EntityReference"/>, which exposes only a GUID and a
    /// Load(company). Rutter links records by platform_id, and for accounts,
    /// customers and vendors that is the Sage **ID** string ("10200-00",
    /// "ANDERSON-01") — not a GUID. So a reference has to be turned into an ID
    /// before it is any use to a mapper.
    ///
    /// Load()ing each reference would be one round trip per line per
    /// transaction. Instead the three lists are read once and indexed by key,
    /// which is the same join Sage's own SDK samples do
    /// (`join vendor in vendorList on payment.VendorReference equals vendor.Key`).
    ///
    /// One flat map covers all three types because Sage keys are GUIDs and so do
    /// not collide. A reference to something not indexed — an inventory item, a
    /// job — simply resolves to null, and those are reported as raw GUIDs
    /// instead.
    /// </summary>
    internal sealed class ReferenceIndex
    {
        private readonly Dictionary<Guid, string> m_idsByKey = new Dictionary<Guid, string>();

        public int Count
        {
            get { return m_idsByKey.Count; }
        }

        public void Add(EntityReference key, string id)
        {
            if (key == null || key.IsEmpty || string.IsNullOrEmpty(id))
            {
                return;
            }

            // Last write wins; Sage should not hand us two entities on one key,
            // and if it does, either ID is equally correct to report.
            m_idsByKey[key.Guid] = id;
        }

        /// <summary>
        /// The Sage ID for a reference, or null when the reference is empty or
        /// points at something this index does not cover. Null is a legitimate
        /// answer and must not be turned into an empty string — "no account" and
        /// "an account whose ID is blank" are different facts.
        /// </summary>
        public string Resolve(EntityReference reference)
        {
            if (reference == null || reference.IsEmpty)
            {
                return null;
            }

            string id;
            return m_idsByKey.TryGetValue(reference.Guid, out id) ? id : null;
        }

        /// <summary>
        /// A reference as a raw GUID string, for entities Rutter does not sync
        /// yet (inventory items, jobs) and for links between transactions, where
        /// the GUID *is* the id we report.
        /// </summary>
        public static string GuidOf(EntityReference reference)
        {
            if (reference == null || reference.IsEmpty)
            {
                return null;
            }

            return reference.Guid.ToString();
        }

        public string ResolveInventoryItem(EntityReference reference)
        {
            return Resolve(reference);
        }
    }
}
