using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Buildings
{
    public class Building : FullAuditedAggregateRoot<Guid>
    {
        public string Name { get; private set; } = default!;

        public string Address { get; private set; } = default!;

        public string? Description { get; private set; }

        protected Building()
        {

        }
        
        public Building(
            Guid id,
            string name,
            string address,
            string? description = null
        ) : base(id)
        {
            Name = name;
            Address = address;
            Description = description;
        }


        public void Update(
            string name,
            string address,
            string? description)
        {
            Name = name;
            Address = address;
            Description = description;
        }
    }
}
