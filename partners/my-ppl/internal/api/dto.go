package api

// ---- requests ----

type createParcelRequest struct {
	OrderID  string       `json:"orderId"`
	Address  addressDTO   `json:"address"`
	Packages []packageDTO `json:"packages"`
}

type addressDTO struct {
	Name        string `json:"name"`
	Line        string `json:"line"`
	City        string `json:"city"`
	PostalCode  string `json:"postalCode"`
	CountryCode string `json:"countryCode"`
}

type packageDTO struct {
	Reference   string `json:"reference"`
	WeightGrams int    `json:"weightGrams"`
}

type createReturnRequest struct {
	OrderID    string       `json:"orderId"`
	CustomerID string       `json:"customerId"`
	Packages   []packageDTO `json:"packages"`
}

type registerWebhookRequest struct {
	ShipmentID  string   `json:"shipmentId"`
	CallbackURL string   `json:"callbackUrl"`
	Events      []string `json:"events"`
}

// ---- responses ----

type createParcelResponse struct {
	ReferenceID string `json:"referenceId"`
	Status      string `json:"status"` // pending
	TestMode    bool   `json:"testMode"`
}

type pollResponse struct {
	ReferenceID    string `json:"referenceId"`
	Status         string `json:"status"` // pending | accepted | rejected
	ParcelID       string `json:"parcelId,omitempty"`
	TrackingNumber string `json:"trackingNumber,omitempty"`
	Reason         string `json:"reason,omitempty"`
	TestMode       bool   `json:"testMode"`
}

type cancelResponse struct {
	ID       string `json:"id"`
	Status   string `json:"status"`
	TestMode bool   `json:"testMode"`
}

type trackingResponse struct {
	TrackingNumber string `json:"trackingNumber"`
	Status         string `json:"status"`
	TestMode       bool   `json:"testMode"`
}

type registerWebhookResponse struct {
	Registered bool `json:"registered"`
}

type createReturnResponse struct {
	ReturnShipmentID     string `json:"returnShipmentId"`
	ReturnTrackingNumber string `json:"returnTrackingNumber"`
	ExpectedPickupDate   string `json:"expectedPickupDate"`
	QrToken              string `json:"qrToken"`
	TestMode             bool   `json:"testMode"`
}

// PPL reports errors as { error, message } (distinct from DPD's { error_code, error_message }).
type errorResponse struct {
	Error   string `json:"error"`
	Message string `json:"message"`
}
