using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Play.Common;
using Play.Trading.Service.Dtos;
using Play.Trading.Service.Entities;

namespace Play.Trading.Service.Controllers;

[ApiController]
[Route("store")]
[Authorize]
public class StoreController : ControllerBase
{
    private readonly IRepository<CatalogItem> _catalogItemRepository;
    private readonly IRepository<ApplicationUser> _userRepository;
    private readonly IRepository<InventoryItem> _inventoryRepository;

    public StoreController(
        IRepository<InventoryItem> inventoryRepository, 
        IRepository<ApplicationUser> userRepository, 
        IRepository<CatalogItem> catalogItemRepository)
    {
        _inventoryRepository = inventoryRepository;
        _userRepository = userRepository;
        _catalogItemRepository = catalogItemRepository;
    }

    [HttpGet]
    public async Task<ActionResult<StoreDto>> GetAsync()
    {
        string userId = User.FindFirstValue("sub");

        var catalogItems = await _catalogItemRepository.GetAllAsync();
        var inventoryItems = await _inventoryRepository.GetAllAsync(item => item.UserId == Guid.Parse(userId));
        var user = await _userRepository.GetAsync(Guid.Parse(userId));

        var storeDto = new StoreDto(
            catalogItems.Select(catalogItem => 
                new StoreItemDto
                (
                    catalogItem.Id, 
                    catalogItem.Name,
                    catalogItem.Description,
                    catalogItem.Price,
                    inventoryItems.FirstOrDefault(
                        inventoryItem => inventoryItem.CatalogItemId == catalogItem.Id)?.Quantity ?? 0
                )
            ),
            user?.Gil ?? 0
        );

        return Ok(storeDto);
    }
}