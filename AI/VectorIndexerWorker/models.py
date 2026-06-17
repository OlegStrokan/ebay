from pydantic import BaseModel, model_validator


class ProductAttribute(BaseModel):
    key: str
    value: str


class ProductEvent(BaseModel):
    product_id: str
    name: str
    description: str | None = None
    category: str | None = None
    price: float = 0.0
    currency: str = "USD"
    stock_quantity: int = 0
    image_urls: list[str] = []
    attributes: list[ProductAttribute] = []

    @model_validator(mode='before')
    @classmethod
    def _coerce_from_pascal_case(cls, v: dict) -> dict:
        """Map PascalCase nested C# serialization to flat snake_case fields."""
        if not isinstance(v, dict) or 'ProductId' not in v:
            return v
        product_id_obj = v.get('ProductId', {})
        price_obj = v.get('Price', {})
        category_id_obj = v.get('CategoryId', {})
        return {
            'product_id': product_id_obj.get('Value', '') if isinstance(product_id_obj, dict) else str(product_id_obj),
            'name': v.get('Name') or '',
            'description': v.get('Description'),
            'category': category_id_obj.get('Value') if isinstance(category_id_obj, dict) else None,
            'price': float(price_obj.get('Amount', 0)) if isinstance(price_obj, dict) else 0.0,
            'currency': price_obj.get('Currency', 'USD') if isinstance(price_obj, dict) else 'USD',
            # ProductCreatedEvent uses InitialStock; ProductUpdatedEvent has no stock field
            'stock_quantity': v.get('InitialStock', 0),
            'image_urls': v.get('ImageUrls', []),
            'attributes': [
                {'key': a.get('Key', ''), 'value': a.get('Value', '')}
                for a in v.get('Attributes', [])
            ],
        }


class ProductStockUpdatedEvent(BaseModel):
    product_id: str
    previous_quantity: int
    new_quantity: int

    @model_validator(mode='before')
    @classmethod
    def _coerce_from_pascal_case(cls, v: dict) -> dict:
        """Map PascalCase nested C# serialization to flat snake_case fields."""
        if not isinstance(v, dict) or 'ProductId' not in v:
            return v
        product_id_obj = v.get('ProductId', {})
        return {
            'product_id': product_id_obj.get('Value', '') if isinstance(product_id_obj, dict) else str(product_id_obj),
            'previous_quantity': v.get('PreviousQuantity', 0),
            'new_quantity': v.get('NewQuantity', 0),
        }


class CatalogItemIdPayload(BaseModel):
    Value: str

class CategoryIdPayload(BaseModel):
    Value: str

class CatalogItemEvent(BaseModel):
    CatalogItemId: CatalogItemIdPayload
    Name: str
    Description: str | None = None
    CategoryId: CategoryIdPayload
    Gtin: str | None = None
    Attributes: list[ProductAttribute] = []
    ImageUrls: list[str] = []

class CatalogItemListingSummaryEvent(BaseModel):
    CatalogItemId: CatalogItemIdPayload
    MinPrice: float = 0.0
    MinPriceCurrency: str = "USD"
    SellerCount: int = 0
    HasActiveListings: bool = False
    BestCondition: str | None = None
    TotalStock: int = 0