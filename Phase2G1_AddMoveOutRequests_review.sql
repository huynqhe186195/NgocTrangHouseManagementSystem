START TRANSACTION;
CREATE TABLE "BuildingManagementMoveOutRequests" (
    "Id" uuid NOT NULL,
    "ContractId" uuid NOT NULL,
    "RoomId" uuid NOT NULL,
    "RequestedMoveOutDate" date NOT NULL,
    "ApprovedMoveOutDate" date,
    "Reason" character varying(1024),
    "Status" integer NOT NULL,
    "ReviewNotes" character varying(1024),
    "ReviewedAt" timestamp with time zone,
    "CancelledAt" timestamp with time zone,
    "CompletedAt" timestamp with time zone,
    "ExtraProperties" text NOT NULL,
    "ConcurrencyStamp" character varying(40) NOT NULL,
    "CreationTime" timestamp with time zone NOT NULL,
    "CreatorId" uuid,
    "LastModificationTime" timestamp with time zone,
    "LastModifierId" uuid,
    "IsDeleted" boolean NOT NULL DEFAULT FALSE,
    "DeleterId" uuid,
    "DeletionTime" timestamp with time zone,
    CONSTRAINT "PK_BuildingManagementMoveOutRequests" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BuildingManagementMoveOutRequests_BuildingManagementContrac~" FOREIGN KEY ("ContractId") REFERENCES "BuildingManagementContracts" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_BuildingManagementMoveOutRequests_BuildingManagementRooms_R~" FOREIGN KEY ("RoomId") REFERENCES "BuildingManagementRooms" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "BuildingManagementMoveOutChecklistTasks" (
    "Id" uuid NOT NULL,
    "MoveOutRequestId" uuid NOT NULL,
    "Type" integer NOT NULL,
    "IsCompleted" boolean NOT NULL,
    "CompletedAt" timestamp with time zone,
    "ExtraProperties" text NOT NULL,
    "ConcurrencyStamp" character varying(40) NOT NULL,
    "CreationTime" timestamp with time zone NOT NULL,
    "CreatorId" uuid,
    "LastModificationTime" timestamp with time zone,
    "LastModifierId" uuid,
    CONSTRAINT "PK_BuildingManagementMoveOutChecklistTasks" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BuildingManagementMoveOutChecklistTasks_BuildingManagementM~" FOREIGN KEY ("MoveOutRequestId") REFERENCES "BuildingManagementMoveOutRequests" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "UX_MoveOutChecklistTask_Request_Type" ON "BuildingManagementMoveOutChecklistTasks" ("MoveOutRequestId", "Type");

CREATE INDEX "IX_BuildingManagementMoveOutRequests_RoomId" ON "BuildingManagementMoveOutRequests" ("RoomId");

CREATE UNIQUE INDEX "UX_MoveOutRequests_OneOpenPerContract" ON "BuildingManagementMoveOutRequests" ("ContractId") WHERE "IsDeleted" = FALSE AND "Status" IN (0, 1);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261022165844_Phase2G1_AddMoveOutRequests', '10.0.9');

COMMIT;

