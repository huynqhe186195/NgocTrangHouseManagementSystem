START TRANSACTION;
CREATE TABLE "BuildingManagementInvoices" (
    "Id" uuid NOT NULL,
    "ContractId" uuid NOT NULL,
    "RoomId" uuid NOT NULL,
    "BillingYear" integer NOT NULL,
    "BillingMonth" integer NOT NULL,
    "Status" integer NOT NULL,
    "ProvisionalUtilityTotal" numeric(18,3),
    "ExtraProperties" text NOT NULL,
    "ConcurrencyStamp" character varying(40) NOT NULL,
    "CreationTime" timestamp with time zone NOT NULL,
    "CreatorId" uuid,
    "LastModificationTime" timestamp with time zone,
    "LastModifierId" uuid,
    CONSTRAINT "PK_BuildingManagementInvoices" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BuildingManagementInvoices_BuildingManagementContracts_Cont~" FOREIGN KEY ("ContractId") REFERENCES "BuildingManagementContracts" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_BuildingManagementInvoices_BuildingManagementRooms_RoomId" FOREIGN KEY ("RoomId") REFERENCES "BuildingManagementRooms" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "BuildingManagementInvoiceItems" (
    "Id" uuid NOT NULL,
    "InvoiceId" uuid NOT NULL,
    "UtilityType" integer NOT NULL,
    "UtilityRateId" uuid,
    "RateStatus" character varying(64) NOT NULL,
    "RecordedQuantity" numeric(18,3) NOT NULL,
    "UnitPrice" numeric(18,0),
    "ProvisionalAmount" numeric(18,3),
    "CoverageIssuesJson" text NOT NULL,
    "CreationTime" timestamp with time zone NOT NULL,
    "CreatorId" uuid,
    "LastModificationTime" timestamp with time zone,
    "LastModifierId" uuid,
    CONSTRAINT "PK_BuildingManagementInvoiceItems" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BuildingManagementInvoiceItems_BuildingManagementInvoices_I~" FOREIGN KEY ("InvoiceId") REFERENCES "BuildingManagementInvoices" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_BuildingManagementInvoiceItems_BuildingManagementUtilityRat~" FOREIGN KEY ("UtilityRateId") REFERENCES "BuildingManagementUtilityRates" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "BuildingManagementInvoiceItemMeterSources" (
    "Id" uuid NOT NULL,
    "InvoiceItemId" uuid NOT NULL,
    "SourceId" uuid NOT NULL,
    "UtilityMeterId" uuid NOT NULL,
    "SourceType" integer NOT NULL,
    "RecordedDate" date NOT NULL,
    "PreviousReading" numeric(18,3) NOT NULL,
    "CurrentReading" numeric(18,3) NOT NULL,
    CONSTRAINT "PK_BuildingManagementInvoiceItemMeterSources" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BuildingManagementInvoiceItemMeterSources_BuildingManagemen~" FOREIGN KEY ("InvoiceItemId") REFERENCES "BuildingManagementInvoiceItems" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_BuildingManagementInvoiceItemMeterSources_BuildingManageme~1" FOREIGN KEY ("UtilityMeterId") REFERENCES "BuildingManagementUtilityMeters" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_BuildingManagementInvoiceItemMeterSources_UtilityMeterId" ON "BuildingManagementInvoiceItemMeterSources" ("UtilityMeterId");

CREATE UNIQUE INDEX "UX_BM_InvoiceSource_Global" ON "BuildingManagementInvoiceItemMeterSources" ("SourceType", "SourceId");

CREATE UNIQUE INDEX "UX_BM_InvoiceSource_Item_Type_Source" ON "BuildingManagementInvoiceItemMeterSources" ("InvoiceItemId", "SourceType", "SourceId");

CREATE INDEX "IX_BuildingManagementInvoiceItems_UtilityRateId" ON "BuildingManagementInvoiceItems" ("UtilityRateId");

CREATE UNIQUE INDEX "UX_BM_InvoiceItem_Invoice_Utility" ON "BuildingManagementInvoiceItems" ("InvoiceId", "UtilityType");

CREATE UNIQUE INDEX "UX_BM_Invoice_Contract_Period" ON "BuildingManagementInvoices" ("ContractId", "BillingYear", "BillingMonth");

CREATE UNIQUE INDEX "UX_BM_Invoice_Room_Period" ON "BuildingManagementInvoices" ("RoomId", "BillingYear", "BillingMonth");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261023182226_Phase2G3A4_AddInvoiceDraftSnapshots', '10.0.9');

COMMIT;

