using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using BuildingManagement.Floors;
using BuildingManagement.Images;
using BuildingManagement.Security;
using System.Linq;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Buildings
{
    public class BuildingAppService :
    ApplicationService,
    IBuildingAppService
    {
        private readonly IRepository<Building, Guid> _buildingRepository;
        private readonly IRepository<Floor, Guid> _floorRepository;
        private readonly IRepository<BuildingPhoto, Guid> _photoRepository;
        private readonly IRepository<BuildingPhotoContent, Guid> _photoContentRepository;
        private readonly IBuildingCrudAccessPolicy _access;
        private readonly BuildingOwnerBootstrapCoordinator _bootstrap;
        private readonly IBuildingScopedWriteBoundary _writeBoundary;

        public BuildingAppService(
            IRepository<Building, Guid> buildingRepository,
            IRepository<Floor, Guid> floorRepository,
            IRepository<BuildingPhoto, Guid> photoRepository,
            IRepository<BuildingPhotoContent, Guid> photoContentRepository,
            IBuildingCrudAccessPolicy access,
            BuildingOwnerBootstrapCoordinator bootstrap,
            IBuildingScopedWriteBoundary writeBoundary)
        {
            _buildingRepository = buildingRepository;
            _floorRepository = floorRepository;
            _photoRepository = photoRepository;
            _photoContentRepository = photoContentRepository;
            _access = access;
            _bootstrap = bootstrap;
            _writeBoundary = writeBoundary;
        }

        public async Task<BuildingDto> CreateAsync(CreateBuildingDto input)
        {
            // Owner and Building MUST be created atomically, not by a
            // second independent InsertAsync that can leave an orphan.
            // The bootstrap writer is default-DENY until reviewed DB
            // membership persistence is available.
            var building = await _bootstrap.CreateWithCurrentOwnerAsync(input);
            return ObjectMapper.Map<Building, BuildingDto>(building);
        }

        public async Task DeleteAsync(Guid id)
        {
            await _access.EnsureBuildingAsync(id, BuildingBusinessCapability.ManageBuilding);
            await _writeBoundary.ExecuteBuildingAsync(id, async () =>
            {
            var building =
                await _buildingRepository.FindAsync(id);

            if (building is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.BuildingNotFound
                );
            }

            await EnsureNoBuildingFloorsAsync(id);

            // Do NOT autoSave here. The write fence revalidates the actor's
            // membership before it saves, and that check reads live
            // Buildings rows: flushing the soft delete first would remove
            // the very grant that authorizes the delete. Leave the change
            // tracked so the fence's SaveChangesAsync persists it after
            // revalidation passes.
            await _buildingRepository.DeleteAsync(
                building,
                autoSave: false
            );
            return true;
            });
        }

        public async Task<BuildingDto> GetAsync(Guid id)
        {
            await _access.EnsureBuildingAsync(id, BuildingBusinessCapability.ReadBuilding);
            var building =
                await _buildingRepository.GetAsync(id);

            var dto = ObjectMapper.Map<Building, BuildingDto>(building);
            dto.HasPhoto = await _photoRepository.AnyAsync(x => x.BuildingId == id);
            return dto;
        }

        public async Task<List<BuildingDto>> GetListAsync()
        {
            var authorized = await _access.GetAuthorizedBuildingIdsAsync(
                BuildingBusinessCapability.ReadBuilding);
            var buildings =
                await _buildingRepository.GetListAsync(b =>
                    authorized.Contains(b.Id));

            var dtos = ObjectMapper.Map<List<Building>, List<BuildingDto>>(buildings);

            // One query for the whole page rather than N point lookups.
            var withPhoto = await _photoRepository.GetListAsync(x =>
                authorized.Contains(x.BuildingId));
            var photoOwners = withPhoto.Select(x => x.BuildingId).ToHashSet();
            foreach (var dto in dtos)
            {
                dto.HasPhoto = photoOwners.Contains(dto.Id);
            }
            return dtos;
        }

        public async Task<BuildingDto> UpdateAsync(
    Guid id,
    UpdateBuildingDto input)
        {
            await _access.EnsureBuildingAsync(id, BuildingBusinessCapability.ManageBuilding);
            return await _writeBoundary.ExecuteBuildingAsync(id, async () =>
            {
            var building =
                await _buildingRepository.GetAsync(id);


            building.Update(
                input.Name,
                input.Address,
                input.Description
            );


            await _buildingRepository.UpdateAsync(
                building
            );


            return ObjectMapper.Map<
                Building,
                BuildingDto
            >(building);
            });
        }

        private async Task EnsureNoBuildingFloorsAsync(Guid buildingId)
        {
            var hasFloors = await _floorRepository.AnyAsync(
                f => f.BuildingId == buildingId
            );

            if (hasFloors)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.BuildingHasFloors
                );
            }
        }

        public async Task<BuildingPhotoDto?> GetPhotoAsync(Guid id)
        {
            await _access.EnsureBuildingAsync(id, BuildingBusinessCapability.ReadBuilding);
            var photo = await _photoRepository.FindAsync(x => x.BuildingId == id);
            return photo is null
                ? null
                : ObjectMapper.Map<BuildingPhoto, BuildingPhotoDto>(photo);
        }

        /// <summary>
        /// Replaces the cover image. A building keeps at most one, so the
        /// previous row and its bytes are removed in the same fence — an
        /// upload can never leave two covers behind.
        /// </summary>
        public async Task<BuildingPhotoDto> UploadPhotoAsync(
            Guid id, UploadBuildingPhotoDto input)
        {
            await _access.EnsureBuildingAsync(id, BuildingBusinessCapability.ManageBuilding);

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(input.Base64Content ?? "");
            }
            catch (FormatException)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidBuildingPhotoContent);
            }

            if (bytes.LongLength > ImagePayloadRules.MaxBytes)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.BuildingPhotoTooLarge);
            }

            // Sniff the payload itself: a client-supplied type is never trusted.
            var contentType = ImagePayloadRules.DetectContentType(bytes);
            if (contentType is null)
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidBuildingPhotoContent);

            return await _writeBoundary.ExecuteBuildingAsync(id, async () =>
            {
                var existing = await _photoRepository.FindAsync(x => x.BuildingId == id);
                if (existing is not null)
                {
                    var oldContent = await _photoContentRepository.FindAsync(existing.Id);
                    if (oldContent is not null)
                    {
                        await _photoContentRepository.DeleteAsync(oldContent, autoSave: false);
                    }
                    await _photoRepository.DeleteAsync(existing, autoSave: false);
                }

                var extension = ImagePayloadRules.ExtensionFor(contentType) ?? ".img";
                var photo = new BuildingPhoto(
                    GuidGenerator.Create(), id,
                    "building" + extension,
                    contentType, bytes.LongLength);
                await _photoRepository.InsertAsync(photo, autoSave: false);
                await _photoContentRepository.InsertAsync(
                    new BuildingPhotoContent(photo.Id, bytes), autoSave: true);
                return ObjectMapper.Map<BuildingPhoto, BuildingPhotoDto>(photo);
            }, BuildingScopedMutationKind.Building);
        }

        public async Task<BuildingPhotoFileDto> GetPhotoContentAsync(Guid id)
        {
            await _access.EnsureBuildingAsync(id, BuildingBusinessCapability.ReadBuilding);
            var photo = await _photoRepository.FindAsync(x => x.BuildingId == id)
                ?? throw new BusinessException(
                    BuildingManagementErrorCodes.BuildingPhotoNotFound);
            var content = await _photoContentRepository.FindAsync(photo.Id)
                ?? throw new BusinessException(
                    BuildingManagementErrorCodes.BuildingPhotoNotFound);
            return new BuildingPhotoFileDto
            {
                Content = content.Content,
                ContentType = photo.ContentType,
                FileName = photo.FileName
            };
        }

        public async Task DeletePhotoAsync(Guid id)
        {
            await _access.EnsureBuildingAsync(id, BuildingBusinessCapability.ManageBuilding);
            await _writeBoundary.ExecuteBuildingAsync(id, async () =>
            {
                var photo = await _photoRepository.FindAsync(x => x.BuildingId == id);
                if (photo is null)
                {
                    return true;
                }
                var content = await _photoContentRepository.FindAsync(photo.Id);
                if (content is not null)
                {
                    await _photoContentRepository.DeleteAsync(content, autoSave: false);
                }
                await _photoRepository.DeleteAsync(photo, autoSave: true);
                return true;
            }, BuildingScopedMutationKind.Building);
        }
    }
}
