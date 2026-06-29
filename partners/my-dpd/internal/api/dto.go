package api

// ---- requests ----

type createShipmentRequest struct {
	OrderID   string       `json:"orderId"`
	Recipient recipientDTO `json:"recipient"`
	Parcels   []parcelDTO  `json:"parcels"`
}

type recipientDTO struct {
	Name        string `json:"name"`
	AddressLine string `json:"addressLine"`
	City        string `json:"city"`
	PostalCode  string `json:"postalCode"`
	CountryCode string `json:"countryCode"`
}

type parcelDTO struct {
	Reference   string  `json:"reference"`
	WeightGrams int     `json:"weightGrams"`
	WidthCm     float64 `json:"widthCm"`
	HeightCm    float64 `json:"heightCm"`
	DepthCm     float64 `json:"depthCm"`
}

type createReturnRequest struct {
	OrderID    string      `json:"orderId"`
	CustomerID string      `json:"customerId"`
	Parcels    []parcelDTO `json:"parcels"`
}

type registerWebhookRequest struct {
	ShipmentID  string   `json:"shipmentId"`
	CallbackURL string   `json:"callbackUrl"`
	Events      []string `json:"events"`
}

// ---- responses ----

type createShipmentResponse struct {
	ShipmentID     string `json:"shipmentId"`
	TrackingNumber string `json:"trackingNumber"`
	Status         string `json:"status"`
	TestMode       bool   `json:"testMode"`
}

type createReturnResponse struct {
	ReturnShipmentID     string `json:"returnShipmentId"`
	ReturnTrackingNumber string `json:"returnTrackingNumber"`
	ExpectedPickupDate   string `json:"expectedPickupDate"`
	TestMode             bool   `json:"testMode"`
}

type registerWebhookResponse struct {
	Registered bool `json:"registered"`
}

type statusResponse struct {
	ShipmentID     string `json:"shipmentId"`
	TrackingNumber string `json:"trackingNumber"`
	Status         string `json:"status"`
	TestMode       bool   `json:"testMode"`
}

type errorResponse struct {
	ErrorCode    string `json:"error_code"`
	ErrorMessage string `json:"error_message"`
}
